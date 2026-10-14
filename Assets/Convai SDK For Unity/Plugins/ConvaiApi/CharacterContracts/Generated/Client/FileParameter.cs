using System.IO;

namespace Convai.CharacterApi.Contracts.Client
{
    public sealed class FileParameter
    {
        public Stream Data { get; set; }
        public string Name { get; set; }
        public string ContentType { get; set; }
    }
}
