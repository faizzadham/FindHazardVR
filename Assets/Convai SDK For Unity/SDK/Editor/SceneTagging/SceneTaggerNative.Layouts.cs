// Generated from the pinned ABI v4 C header. Regenerate when the ABI changes.
using System;
using System.Runtime.InteropServices;
namespace Convai.Editor.SceneTagging
{
    internal static partial class SceneTaggerNative
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_error_v1
        {
            public uint @struct_size;
            public int @code;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] @message;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_image_view_v1
        {
            public uint @struct_size;
            public uint @width;
            public uint @height;
            public uint @row_stride_bytes;
            public uint @pixel_format;
            public IntPtr @pixels;
            public ulong @pixel_bytes;
            public IntPtr @geometry_mask;
            public uint @geometry_mask_row_stride_bytes;
            public uint @background_epsilon;
            public ulong @geometry_mask_bytes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] @background_rgba;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_view_metrics_v1
        {
            public uint @struct_size;
            public uint @valid;
            public int @foreground_pixels;
            public int @interior_pixels;
            public float @foreground_fraction;
            public float @border_touch_fraction;
            public float @color_entropy;
            public float @edge_detail;
            public float @silhouette_information;
            public uint @shape_profile_candidate;
            public int @central_foreground_pixels;
            public float @central_tone_entropy;
            public float @central_color_entropy;
            public float @central_edge_detail;
            public float @central_foreground_fill;
            public float @central_authored_content;
            public float @capture_quality;
            public float @front_preference;
            public float @selection_score;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_evidence_descriptor_v1
        {
            public uint @struct_size;
            public uint @descriptor_version;
            public uint @payload_size;
            public uint @reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5128)]
            public byte[] @payload;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vec3d
        {
            public double @x;
            public double @y;
            public double @z;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_component_proxy_v1
        {
            public uint @struct_size;
            public uint @point_count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public convai_sat_vec3d[] @points;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_view_plan_input_v1
        {
            public uint @struct_size;
            public uint @flags;
            public uint @component_count;
            public uint @reserved0;
            public IntPtr @components;
            public convai_sat_vec3d @presentation_direction_actor_local;
            public float @presentation_preference;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_view_candidate_v1
        {
            public convai_sat_vec3d @direction_actor_local;
            public float @front_preference;
            public uint @flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_view_plan_v1
        {
            public uint @struct_size;
            public uint @candidate_count;
            public uint @flags;
            public uint @lighting_policy;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public convai_sat_view_candidate_v1[] @candidates;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_select_view_input_v4
        {
            public uint @struct_size;
            public uint @image_count;
            public IntPtr @plan;
            public IntPtr @images;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_select_view_result_v4
        {
            public uint @struct_size;
            public int @selected_index;
            public float @selection_margin;
            public float @selected_score;
            public uint @metric_count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public convai_sat_view_metrics_v1[] @metrics;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_evaluate_capture_input_v4
        {
            public uint @struct_size;
            public uint @flags;
            public float @significance_score;
            public uint @primitive_count;
            public uint @significance_reason_flags;
            public uint @probe_metric_count;
            public IntPtr @probe_metrics;
            public convai_sat_image_view_v1 @final_image;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_evaluate_capture_result_v4
        {
            public uint @struct_size;
            public uint @decision;
            public uint @reason;
            public uint @readable_centered_coverage;
            public uint @informative_evidence;
            public uint @meaningful_mid_scale_structure;
            public uint @descriptor_valid;
            public uint @reserved0;
            public convai_sat_evidence_descriptor_v1 @descriptor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_kv_v4
        {
            public IntPtr @key_utf8;
            public IntPtr @value_utf8;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_cell_v3
        {
            public uint @struct_size;
            public uint @context_only;
            public IntPtr @echo_id_utf8;
            public IntPtr @previous_proposal_name_utf8;
            public IntPtr @previous_proposal_description_utf8;
            public IntPtr @refinement_note_utf8;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_image_v3
        {
            public uint @struct_size;
            public uint @cell_count;
            public IntPtr @file_name_utf8;
            public IntPtr @png_bytes;
            public ulong @png_size;
            public IntPtr @cells;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_autotag_request_v4
        {
            public uint @struct_size;
            public uint @task;
            public IntPtr @scene_description_utf8;
            public IntPtr @description_focus_utf8;
            public uint @image_count;
            public int @timeout_seconds;
            public IntPtr @images;
            public uint @param_count;
            public uint @reserved0;
            public IntPtr @params;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_request_v4
        {
            public uint @struct_size;
            public uint @image_count;
            public IntPtr @prompt_utf8;
            public int @timeout_seconds;
            public uint @reserved0;
            public IntPtr @images;
            public uint @param_count;
            public uint @reserved1;
            public IntPtr @params;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_object_view_v3
        {
            public uint @struct_size;
            public int @cell_index;
            public float @confidence;
            public uint @reserved0;
            public IntPtr @echo_id_utf8;
            public IntPtr @name_utf8;
            public IntPtr @description_utf8;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_result_image_v4
        {
            public uint @struct_size;
            public uint @object_count;
            public IntPtr @file_name_utf8;
            public IntPtr @objects;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_vision_result_view_v4
        {
            public uint @struct_size;
            public int @http_status;
            public uint @image_count;
            public uint @reserved0;
            public IntPtr @server_message_utf8;
            public IntPtr @raw_body_utf8;
            public ulong @raw_body_size;
            public IntPtr @images;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] @reserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct convai_sat_api_v4
        {
            public uint @struct_size;
            public uint @abi_version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public uint[] @reserved;
            public IntPtr @core_version_utf8;
            public IntPtr @view_policy_id_utf8;
            public IntPtr @vision_protocol_id_utf8;
            public IntPtr @build_view_plan;
            public IntPtr @select_view;
            public IntPtr @evaluate_capture;
            public IntPtr @compare_evidence;
            public IntPtr @vision_autotag;
            public IntPtr @vision_request;
            public IntPtr @vision_result_view;
            public IntPtr @vision_result_free;
        }
        internal static void ValidateLayouts()
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Scene Tagger requires a 64-bit Editor.");
            if (Marshal.SizeOf<convai_sat_error_v1>() != 264) throw new InvalidOperationException("ABI size mismatch: convai_sat_error_v1");
            if (Marshal.SizeOf<convai_sat_image_view_v1>() != 88) throw new InvalidOperationException("ABI size mismatch: convai_sat_image_view_v1");
            if (Marshal.SizeOf<convai_sat_view_metrics_v1>() != 108) throw new InvalidOperationException("ABI size mismatch: convai_sat_view_metrics_v1");
            if (Marshal.SizeOf<convai_sat_evidence_descriptor_v1>() != 5144) throw new InvalidOperationException("ABI size mismatch: convai_sat_evidence_descriptor_v1");
            if (Marshal.SizeOf<convai_sat_vec3d>() != 24) throw new InvalidOperationException("ABI size mismatch: convai_sat_vec3d");
            if (Marshal.SizeOf<convai_sat_component_proxy_v1>() != 216) throw new InvalidOperationException("ABI size mismatch: convai_sat_component_proxy_v1");
            if (Marshal.SizeOf<convai_sat_view_plan_input_v1>() != 72) throw new InvalidOperationException("ABI size mismatch: convai_sat_view_plan_input_v1");
            if (Marshal.SizeOf<convai_sat_view_candidate_v1>() != 48) throw new InvalidOperationException("ABI size mismatch: convai_sat_view_candidate_v1");
            if (Marshal.SizeOf<convai_sat_view_plan_v1>() != 240) throw new InvalidOperationException("ABI size mismatch: convai_sat_view_plan_v1");
            if (Marshal.SizeOf<convai_sat_select_view_input_v4>() != 40) throw new InvalidOperationException("ABI size mismatch: convai_sat_select_view_input_v4");
            if (Marshal.SizeOf<convai_sat_select_view_result_v4>() != 468) throw new InvalidOperationException("ABI size mismatch: convai_sat_select_view_result_v4");
            if (Marshal.SizeOf<convai_sat_evaluate_capture_input_v4>() != 136) throw new InvalidOperationException("ABI size mismatch: convai_sat_evaluate_capture_input_v4");
            if (Marshal.SizeOf<convai_sat_evaluate_capture_result_v4>() != 5192) throw new InvalidOperationException("ABI size mismatch: convai_sat_evaluate_capture_result_v4");
            if (Marshal.SizeOf<convai_sat_kv_v4>() != 16) throw new InvalidOperationException("ABI size mismatch: convai_sat_kv_v4");
            if (Marshal.SizeOf<convai_sat_vision_cell_v3>() != 56) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_cell_v3");
            if (Marshal.SizeOf<convai_sat_vision_image_v3>() != 56) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_image_v3");
            if (Marshal.SizeOf<convai_sat_vision_autotag_request_v4>() != 72) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_autotag_request_v4");
            if (Marshal.SizeOf<convai_sat_vision_request_v4>() != 64) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_request_v4");
            if (Marshal.SizeOf<convai_sat_vision_object_view_v3>() != 56) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_object_view_v3");
            if (Marshal.SizeOf<convai_sat_vision_result_image_v4>() != 40) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_result_image_v4");
            if (Marshal.SizeOf<convai_sat_vision_result_view_v4>() != 64) throw new InvalidOperationException("ABI size mismatch: convai_sat_vision_result_view_v4");
            if (Marshal.SizeOf<convai_sat_api_v4>() != 104) throw new InvalidOperationException("ABI size mismatch: convai_sat_api_v4");
            if (Marshal.OffsetOf<convai_sat_image_view_v1>("pixels").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_image_view_v1.pixels");
            if (Marshal.OffsetOf<convai_sat_image_view_v1>("geometry_mask").ToInt64() != 40) throw new InvalidOperationException("ABI offset mismatch: convai_sat_image_view_v1.geometry_mask");
            if (Marshal.OffsetOf<convai_sat_view_metrics_v1>("selection_score").ToInt64() != 72) throw new InvalidOperationException("ABI offset mismatch: convai_sat_view_metrics_v1.selection_score");
            if (Marshal.OffsetOf<convai_sat_view_plan_input_v1>("components").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_view_plan_input_v1.components");
            if (Marshal.OffsetOf<convai_sat_view_plan_v1>("candidates").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_view_plan_v1.candidates");
            if (Marshal.OffsetOf<convai_sat_select_view_input_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_input_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_select_view_input_v4>("image_count").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_input_v4.image_count");
            if (Marshal.OffsetOf<convai_sat_select_view_input_v4>("plan").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_input_v4.plan");
            if (Marshal.OffsetOf<convai_sat_select_view_input_v4>("images").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_input_v4.images");
            if (Marshal.OffsetOf<convai_sat_select_view_input_v4>("reserved").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_input_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("selected_index").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.selected_index");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("selection_margin").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.selection_margin");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("selected_score").ToInt64() != 12) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.selected_score");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("metric_count").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.metric_count");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("metrics").ToInt64() != 20) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.metrics");
            if (Marshal.OffsetOf<convai_sat_select_view_result_v4>("reserved").ToInt64() != 452) throw new InvalidOperationException("ABI offset mismatch: convai_sat_select_view_result_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("flags").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.flags");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("significance_score").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.significance_score");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("primitive_count").ToInt64() != 12) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.primitive_count");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("significance_reason_flags").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.significance_reason_flags");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("probe_metric_count").ToInt64() != 20) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.probe_metric_count");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("probe_metrics").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.probe_metrics");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("final_image").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.final_image");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_input_v4>("reserved").ToInt64() != 120) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_input_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("decision").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.decision");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("reason").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.reason");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("readable_centered_coverage").ToInt64() != 12) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.readable_centered_coverage");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("informative_evidence").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.informative_evidence");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("meaningful_mid_scale_structure").ToInt64() != 20) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.meaningful_mid_scale_structure");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("descriptor_valid").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.descriptor_valid");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("reserved0").ToInt64() != 28) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.reserved0");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("descriptor").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.descriptor");
            if (Marshal.OffsetOf<convai_sat_evaluate_capture_result_v4>("reserved").ToInt64() != 5176) throw new InvalidOperationException("ABI offset mismatch: convai_sat_evaluate_capture_result_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_kv_v4>("key_utf8").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_kv_v4.key_utf8");
            if (Marshal.OffsetOf<convai_sat_kv_v4>("value_utf8").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_kv_v4.value_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_cell_v3>("echo_id_utf8").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_cell_v3.echo_id_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_image_v3>("png_bytes").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_image_v3.png_bytes");
            if (Marshal.OffsetOf<convai_sat_vision_image_v3>("cells").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_image_v3.cells");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("task").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.task");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("scene_description_utf8").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.scene_description_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("description_focus_utf8").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.description_focus_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("image_count").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.image_count");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("timeout_seconds").ToInt64() != 28) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.timeout_seconds");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("images").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.images");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("param_count").ToInt64() != 40) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.param_count");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("reserved0").ToInt64() != 44) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.reserved0");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("params").ToInt64() != 48) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.params");
            if (Marshal.OffsetOf<convai_sat_vision_autotag_request_v4>("reserved").ToInt64() != 56) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_autotag_request_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("image_count").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.image_count");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("prompt_utf8").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.prompt_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("timeout_seconds").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.timeout_seconds");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("reserved0").ToInt64() != 20) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.reserved0");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("images").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.images");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("param_count").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.param_count");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("reserved1").ToInt64() != 36) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.reserved1");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("params").ToInt64() != 40) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.params");
            if (Marshal.OffsetOf<convai_sat_vision_request_v4>("reserved").ToInt64() != 48) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_request_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_vision_object_view_v3>("echo_id_utf8").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_object_view_v3.echo_id_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_result_image_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_image_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_vision_result_image_v4>("object_count").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_image_v4.object_count");
            if (Marshal.OffsetOf<convai_sat_vision_result_image_v4>("file_name_utf8").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_image_v4.file_name_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_result_image_v4>("objects").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_image_v4.objects");
            if (Marshal.OffsetOf<convai_sat_vision_result_image_v4>("reserved").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_image_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("http_status").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.http_status");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("image_count").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.image_count");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("reserved0").ToInt64() != 12) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.reserved0");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("server_message_utf8").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.server_message_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("raw_body_utf8").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.raw_body_utf8");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("raw_body_size").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.raw_body_size");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("images").ToInt64() != 40) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.images");
            if (Marshal.OffsetOf<convai_sat_vision_result_view_v4>("reserved").ToInt64() != 48) throw new InvalidOperationException("ABI offset mismatch: convai_sat_vision_result_view_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_api_v4>("struct_size").ToInt64() != 0) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.struct_size");
            if (Marshal.OffsetOf<convai_sat_api_v4>("abi_version").ToInt64() != 4) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.abi_version");
            if (Marshal.OffsetOf<convai_sat_api_v4>("reserved").ToInt64() != 8) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.reserved");
            if (Marshal.OffsetOf<convai_sat_api_v4>("core_version_utf8").ToInt64() != 16) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.core_version_utf8");
            if (Marshal.OffsetOf<convai_sat_api_v4>("view_policy_id_utf8").ToInt64() != 24) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.view_policy_id_utf8");
            if (Marshal.OffsetOf<convai_sat_api_v4>("vision_protocol_id_utf8").ToInt64() != 32) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.vision_protocol_id_utf8");
            if (Marshal.OffsetOf<convai_sat_api_v4>("build_view_plan").ToInt64() != 40) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.build_view_plan");
            if (Marshal.OffsetOf<convai_sat_api_v4>("select_view").ToInt64() != 48) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.select_view");
            if (Marshal.OffsetOf<convai_sat_api_v4>("evaluate_capture").ToInt64() != 56) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.evaluate_capture");
            if (Marshal.OffsetOf<convai_sat_api_v4>("compare_evidence").ToInt64() != 64) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.compare_evidence");
            if (Marshal.OffsetOf<convai_sat_api_v4>("vision_autotag").ToInt64() != 72) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.vision_autotag");
            if (Marshal.OffsetOf<convai_sat_api_v4>("vision_request").ToInt64() != 80) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.vision_request");
            if (Marshal.OffsetOf<convai_sat_api_v4>("vision_result_view").ToInt64() != 88) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.vision_result_view");
            if (Marshal.OffsetOf<convai_sat_api_v4>("vision_result_free").ToInt64() != 96) throw new InvalidOperationException("ABI offset mismatch: convai_sat_api_v4.vision_result_free");
        }
    }
}
