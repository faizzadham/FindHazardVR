using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using static Convai.Editor.SceneTagging.SceneTaggerNative;

namespace Convai.Editor.SceneTagging
{
    internal sealed class SceneTaggerCoreAdapter
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetApi(uint abi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PlanCall(ref convai_sat_view_plan_input_v1 input, ref convai_sat_view_plan_v1 output, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SelectCall(ref convai_sat_select_view_input_v4 input, ref convai_sat_select_view_result_v4 output, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateCall(ref convai_sat_evaluate_capture_input_v4 input, ref convai_sat_evaluate_capture_result_v4 output, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CompareCall(ref convai_sat_evidence_descriptor_v1 left, ref convai_sat_evidence_descriptor_v1 right, out uint equivalent, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnalyzeCall(ref convai_sat_vision_autotag_request_v4 request, out IntPtr result, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ViewCall(IntPtr result, ref convai_sat_vision_result_view_v4 view, ref convai_sat_error_v1 error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCall(IntPtr result);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlopen(string path, int flags);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlsym(IntPtr module, string name);

        private static SceneTaggerCoreAdapter _instance;
        private static int _activeCalls;
        private static bool _quitting;
        private readonly PlanCall _plan;
        private readonly SelectCall _select;
        private readonly EvaluateCall _evaluate;
        private readonly CompareCall _compare;
        private readonly AnalyzeCall _analyze;
        private readonly ViewCall _view;
        private readonly FreeCall _free;
        private readonly IntPtr _library; // Deliberately retained for the Editor process. Replacement requires restart.
        internal string Version { get; }
        internal string Policy { get; }
        internal string Protocol { get; }
        internal static bool HasActiveCalls => _activeCalls != 0;
        internal static SceneTaggerCoreAdapter Instance => _instance ??= new SceneTaggerCoreAdapter();

        private SceneTaggerCoreAdapter()
        {
            ValidateLayouts();
            string root = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SceneTaggerCoreAdapter).Assembly).resolvedPath, "Plugins/SceneAutoTaggerCore");
            bool windows = UnityEngine.Application.platform == RuntimePlatform.WindowsEditor;
            if (!windows && UnityEngine.Application.platform != RuntimePlatform.OSXEditor)
                throw new PlatformNotSupportedException("Scene Auto Tagger supports Windows and macOS Editors.");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "core.lock.json")));
            string relative = windows ? "Editor/Windows/x86_64/convai_scene_auto_tagger_core.dll" : "Editor/macOS/libconvai_scene_auto_tagger_core.dylib";
            string helper = windows ? "Editor/Windows/x86_64/convai_http_helper.dll" : "Editor/macOS/libconvai_http_helper.dylib";
            foreach (string file in new[] { relative, helper }) ValidateLibrary(Path.Combine(root, file), (string)manifest["files"]?[file]);
            string absolute = Path.Combine(root, relative);
            _library = windows ? LoadLibraryExW(absolute, IntPtr.Zero, 8) : dlopen(absolute, 2);
            if (_library == IntPtr.Zero) throw new InvalidOperationException("Cannot load Scene Tagger Core for this Editor architecture. Reinstall and restart Unity.");
            IntPtr entry = windows ? GetProcAddress(_library, "convai_sat_get_api") : dlsym(_library, "convai_sat_get_api");
            if (entry == IntPtr.Zero) throw new InvalidOperationException("Scene Tagger Core entry point is missing.");
            IntPtr table = Marshal.GetDelegateForFunctionPointer<GetApi>(entry)(4);
            if (table == IntPtr.Zero || Marshal.ReadInt32(table) < 104) throw new InvalidOperationException("Scene Tagger Core does not support ABI 4.");
            var api = Marshal.PtrToStructure<convai_sat_api_v4>(table);
            if (api.abi_version != 4) throw new InvalidOperationException("Scene Tagger Core ABI mismatch.");
            Version = Utf8(api.core_version_utf8, 256);
            Policy = Utf8(api.view_policy_id_utf8, 256);
            Protocol = Utf8(api.vision_protocol_id_utf8, 256);
            if (Version != (string)manifest["version"] || Policy != (string)manifest["viewPolicy"] || Protocol != (string)manifest["visionProtocol"])
                throw new InvalidOperationException("The loaded Scene Tagger Core differs from the package lock. Restart Unity after updating the package.");
            _plan = Bind<PlanCall>(api.build_view_plan);
            _select = Bind<SelectCall>(api.select_view);
            _evaluate = Bind<EvaluateCall>(api.evaluate_capture);
            _compare = Bind<CompareCall>(api.compare_evidence);
            _analyze = Bind<AnalyzeCall>(api.vision_autotag);
            _view = Bind<ViewCall>(api.vision_result_view);
            _free = Bind<FreeCall>(api.vision_result_free);
            if (api.vision_request == IntPtr.Zero) throw new InvalidOperationException("Incomplete Scene Tagger function table.");
            EditorApplication.wantsToQuit += () =>
            {
                if (!HasActiveCalls) return true;
                _quitting = true;
                SceneTaggerSession.Current.Cancel();
                return false;
            };
        }

        internal static void ValidateLibrary(string path, string expectedHash)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Reinstall the Scene Tagger native libraries and restart Unity.", Path.GetFileName(path));
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (actual != expectedHash) throw new InvalidOperationException("Scene Tagger library checksum mismatch: " + Path.GetFileName(path));
        }

        private static T Bind<T>(IntPtr pointer) where T : Delegate => pointer == IntPtr.Zero
            ? throw new InvalidOperationException("Incomplete Scene Tagger function table.") : Marshal.GetDelegateForFunctionPointer<T>(pointer);

        internal static convai_sat_vec3d ToCore(Vector3 p, bool position = false) => new()
        { x = p.z * (position ? 100d : 1d), y = -p.x * (position ? 100d : 1d), z = p.y * (position ? 100d : 1d) };
        internal static Vector3 FromCore(convai_sat_vec3d p) => new((float)-p.y, (float)p.z, (float)p.x);

        internal convai_sat_view_plan_v1 Plan(IReadOnlyList<Vector3[]> corners, bool metallic, Vector3? presentationDirection = null)
        {
            using var memory = new Memory();
            var proxies = corners.Select(points =>
            {
                var proxy = New<convai_sat_component_proxy_v1>();
                proxy.point_count = (uint)points.Length;
                for (int i = 0; i < points.Length; i++) proxy.points[i] = ToCore(points[i], true);
                return proxy;
            }).ToArray();
            var input = New<convai_sat_view_plan_input_v1>();
            input.component_count = (uint)proxies.Length;
            input.components = memory.Array(proxies);
            input.flags = 4u | (metallic ? 2u : 0u);
            if (presentationDirection.HasValue)
            {
                input.flags |= 1u;
                input.presentation_direction_actor_local = ToCore(presentationDirection.Value.normalized);
                input.presentation_preference = 1;
            }
            var result = New<convai_sat_view_plan_v1>();
            var error = New<convai_sat_error_v1>();
            Check(_plan(ref input, ref result, ref error), error);
            if (result.candidate_count == 0 || result.candidate_count > 4)
                throw new InvalidOperationException("Core returned an invalid camera plan.");
            return result;
        }

        internal convai_sat_select_view_result_v4 Select(convai_sat_view_plan_v1 plan, IReadOnlyList<SceneTaggerPixels> images)
        {
            using var memory = new Memory();
            var input = New<convai_sat_select_view_input_v4>();
            input.plan = memory.Value(plan);
            input.image_count = (uint)images.Count;
            input.images = memory.Array(images.Select(i => Image(memory, i)).ToArray());
            var output = New<convai_sat_select_view_result_v4>();
            var error = New<convai_sat_error_v1>();
            Check(_select(ref input, ref output, ref error), error);
            if (output.selected_index < -1 || output.selected_index >= plan.candidate_count)
                throw new InvalidOperationException("Core selected an invalid camera view.");
            return output;
        }

        internal convai_sat_evaluate_capture_result_v4 Evaluate(SceneTaggerPixels pixels, convai_sat_view_metrics_v1[] metrics,
            float significance, uint reasons, int primitives, uint flags)
        {
            using var memory = new Memory();
            var input = New<convai_sat_evaluate_capture_input_v4>();
            input.flags = flags;
            input.significance_score = significance;
            input.significance_reason_flags = reasons;
            input.primitive_count = (uint)primitives;
            input.probe_metric_count = (uint)(metrics?.Length ?? 0);
            input.probe_metrics = memory.Array(metrics ?? System.Array.Empty<convai_sat_view_metrics_v1>());
            input.final_image = Image(memory, pixels);
            var output = New<convai_sat_evaluate_capture_result_v4>();
            var error = New<convai_sat_error_v1>();
            Check(_evaluate(ref input, ref output, ref error), error);
            return output;
        }

        internal bool Equivalent(convai_sat_evidence_descriptor_v1 left, convai_sat_evidence_descriptor_v1 right)
        {
            var error = New<convai_sat_error_v1>();
            Check(_compare(ref left, ref right, out uint equivalent, ref error), error);
            return equivalent != 0;
        }

        internal async Task<SceneTaggerResponse> AnalyzeAsync(SceneTaggerRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_quitting) throw new OperationCanceledException();
            if (_activeCalls++ == 0) EditorApplication.LockReloadAssemblies();
            try
            {
                // Do not pass the token to Task.Run: the worker owns all native buffers until return.
                SceneTaggerResponse response = await Task.Run(() => Analyze(request));
                cancellation.ThrowIfCancellationRequested();
                return response;
            }
            finally
            {
                if (--_activeCalls == 0)
                {
                    EditorApplication.UnlockReloadAssemblies();
                    if (_quitting)
                    {
                        _quitting = false;
                        Debug.Log("Scene Auto Tagger requests finished. Unity can now be closed normally.");
                    }
                }
            }
        }

        private SceneTaggerResponse Analyze(SceneTaggerRequest request)
        {
            using var memory = new Memory();
            var cells = request.Cells.Select(c =>
            {
                var cell = New<convai_sat_vision_cell_v3>();
                cell.echo_id_utf8 = memory.String(c.Id);
                cell.context_only = c.ContextOnly ? 1u : 0u;
                cell.previous_proposal_name_utf8 = memory.String(c.Name);
                cell.previous_proposal_description_utf8 = memory.String(c.Description);
                cell.refinement_note_utf8 = memory.String(c.Note);
                return cell;
            }).ToArray();
            var image = New<convai_sat_vision_image_v3>();
            image.file_name_utf8 = memory.String("objects.png");
            image.png_bytes = memory.Bytes(request.Png);
            image.png_size = (ulong)request.Png.Length;
            image.cell_count = (uint)cells.Length;
            image.cells = memory.Array(cells);
            var input = New<convai_sat_vision_autotag_request_v4>();
            input.task = request.SceneContext ? 2u : 0u;
            input.scene_description_utf8 = memory.String(request.SceneDescription);
            input.description_focus_utf8 = memory.String(request.Focus);
            input.image_count = 1;
            input.images = memory.Value(image);
            input.timeout_seconds = request.TimeoutSeconds;
            var parameters = new Dictionary<string, string> { ["api_key"] = request.ApiKey, ["character_id"] = request.CharacterId, ["base_url"] = request.BaseUrl };
            input.param_count = (uint)parameters.Count;
            input.@params = memory.Array(parameters.Select(p => new convai_sat_kv_v4 { key_utf8 = memory.String(p.Key), value_utf8 = memory.String(p.Value) }).ToArray());
            var error = New<convai_sat_error_v1>();
            IntPtr handle = IntPtr.Zero;
            try
            {
                int status = _analyze(ref input, out handle, ref error);
                if (status != 0) return new SceneTaggerResponse { HttpStatus = 0, CanRetryTransport = status == 7, Error = status == 6 ? "The HTTP helper could not load. Reinstall and restart Unity." : "Vision request failed (native status " + status + ")." };
                var view = New<convai_sat_vision_result_view_v4>();
                Check(_view(handle, ref view, ref error), error);
                var response = new SceneTaggerResponse { HttpStatus = view.http_status };
                if (view.http_status < 200 || view.http_status >= 300)
                {
                    response.Error = "Vision returned HTTP " + view.http_status + ".";
                    return response; // Never surface server bodies that may echo credentials or private inputs.
                }
                if (view.image_count != 1 || view.images == IntPtr.Zero) throw new InvalidDataException("Vision returned an unexpected image count.");
                var resultImage = Marshal.PtrToStructure<convai_sat_vision_result_image_v4>(view.images);
                if (Utf8(resultImage.file_name_utf8) != "objects.png" || resultImage.object_count > 64 || resultImage.object_count > 0 && resultImage.objects == IntPtr.Zero)
                    throw new InvalidDataException("Vision returned an unexpected image mapping.");
                for (int i = 0; i < resultImage.object_count; i++)
                {
                    var item = Marshal.PtrToStructure<convai_sat_vision_object_view_v3>(IntPtr.Add(resultImage.objects, i * Marshal.SizeOf<convai_sat_vision_object_view_v3>()));
                    response.Objects.Add(new SceneTaggerObjectResult { Index = item.cell_index, Id = Utf8(item.echo_id_utf8), Name = Utf8(item.name_utf8), Description = Utf8(item.description_utf8), Confidence = item.confidence });
                }
                return response;
            }
            finally { if (handle != IntPtr.Zero) _free(handle); }
        }

        private static convai_sat_image_view_v1 Image(Memory memory, SceneTaggerPixels pixels)
        {
            var image = New<convai_sat_image_view_v1>();
            if (pixels == null) return image;
            image.width = (uint)pixels.Width;
            image.height = (uint)pixels.Height;
            image.row_stride_bytes = image.width * 4;
            image.pixel_format = 2;
            image.pixels = memory.Bytes(pixels.Rgba);
            image.pixel_bytes = (ulong)pixels.Rgba.Length;
            image.geometry_mask = memory.Bytes(pixels.Mask);
            image.geometry_mask_bytes = (ulong)(pixels.Mask?.Length ?? 0);
            image.geometry_mask_row_stride_bytes = image.width;
            image.background_epsilon = 12;
            image.background_rgba = new byte[] { 30, 30, 30, 255 };
            return image;
        }

        internal static T New<T>() where T : struct => (T)Initialized(typeof(T));
        private static object Initialized(Type type)
        {
            object value = Activator.CreateInstance(type);
            foreach (FieldInfo field in type.GetFields())
            {
                if (field.Name == "struct_size") field.SetValue(value, (uint)Marshal.SizeOf(type));
                else if (field.FieldType.IsArray)
                {
                    int length = field.GetCustomAttribute<MarshalAsAttribute>().SizeConst;
                    Type itemType = field.FieldType.GetElementType();
                    Array array = System.Array.CreateInstance(itemType, length);
                    if (!itemType.IsPrimitive) for (int i = 0; i < length; i++) array.SetValue(Initialized(itemType), i);
                    field.SetValue(value, array);
                }
                else if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && field.FieldType != typeof(IntPtr)) field.SetValue(value, Initialized(field.FieldType));
            }
            return value;
        }
        private static void Check(int status, convai_sat_error_v1 error)
        {
            if (status != 0) throw new InvalidOperationException("Scene Tagger Core error " + status + ": " + Encoding.UTF8.GetString(error.message).TrimEnd('\0'));
        }
        private static string Utf8(IntPtr pointer, int max = 65536)
        {
            if (pointer == IntPtr.Zero) return "";
            int size = 0;
            while (size < max && Marshal.ReadByte(pointer, size) != 0) size++;
            if (size == max) throw new InvalidDataException("Unbounded native string.");
            byte[] bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes);
        }
        private sealed class Memory : IDisposable
        {
            private readonly List<IntPtr> _allocations = new();
            public IntPtr Bytes(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) return IntPtr.Zero;
                IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
                _allocations.Add(pointer);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                return pointer;
            }
            public IntPtr String(string text) => string.IsNullOrEmpty(text) ? IntPtr.Zero : Bytes(Encoding.UTF8.GetBytes(text + "\0"));
            public IntPtr Value<T>(T value) where T : struct => Array(new[] { value });
            public IntPtr Array<T>(T[] values) where T : struct
            {
                if (values.Length == 0) return IntPtr.Zero;
                int size = Marshal.SizeOf<T>();
                IntPtr pointer = Marshal.AllocHGlobal(checked(size * values.Length));
                _allocations.Add(pointer);
                for (int i = 0; i < values.Length; i++) Marshal.StructureToPtr(values[i], IntPtr.Add(pointer, i * size), false);
                return pointer;
            }
            public void Dispose() { foreach (IntPtr pointer in _allocations) Marshal.FreeHGlobal(pointer); }
        }
    }

    internal sealed class SceneTaggerPixels
    {
        public int Width, Height;
        public byte[] Rgba, Mask;
    }
    internal sealed class SceneTaggerCell
    {
        public string Id, Name, Description, Note;
        public bool ContextOnly;
    }
    internal sealed class SceneTaggerRequest
    {
        public string ApiKey, CharacterId, BaseUrl, SceneDescription, Focus;
        public byte[] Png;
        public SceneTaggerCell[] Cells;
        public bool SceneContext;
        public int TimeoutSeconds = 120;
    }
    internal sealed class SceneTaggerObjectResult
    {
        public int Index;
        public string Id, Name, Description;
        public float Confidence;
    }
    internal sealed class SceneTaggerResponse
    {
        public int HttpStatus;
        public bool CanRetryTransport = true;
        public string Error;
        public readonly List<SceneTaggerObjectResult> Objects = new();
    }
}
