using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Antril.Pack
{
    /// <summary>
    /// The entry points Larva calls in a language pack (<c>abi</c> 1 of the manifest):
    /// <list type="bullet">
    /// <item><c>larva_language_call(request, length, &amp;resultLength)</c>: one request (UTF-8 JSON), its answer (UTF-8
    /// JSON, allocated here).</item>
    /// <item><c>larva_language_free(result)</c>: releases an answer.</item>
    /// </list>
    /// Published natively they are the library's exports; in a working copy of larva (not native) the same methods are
    /// found by their entry point names and called through their addresses.
    /// </summary>
    public static unsafe class LanguageExports
    {
        private static readonly AntrilLanguage language = new();

        [UnmanagedCallersOnly(EntryPoint = "larva_language_call")]
        public static byte* Call(byte* request, int length, int* resultLength)
        {
            JsonObject response;
            try
            {
                var node = JsonNode.Parse(new ReadOnlySpan<byte>(request, length)) as JsonObject ?? throw new LanguageException("antril: a request is a JSON object");
                lock (language)
                    response = language.Handle(node);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                response = new JsonObject { ["error"] = e.Message };
            }
            byte[] bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            byte* result = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            bytes.CopyTo(new Span<byte>(result, bytes.Length));
            *resultLength = bytes.Length;
            return result;
        }

        [UnmanagedCallersOnly(EntryPoint = "larva_language_free")]
        public static void Free(byte* result) => NativeMemory.Free(result);
    }
}
