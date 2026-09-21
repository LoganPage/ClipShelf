using System.Threading.Tasks;

namespace ClipShelf;

// Keep the established test command, but never start an Office helper to produce fixtures.
internal static class PreviewInteractionTests
{
    internal static Task Run(string root) => NativePreviewTests.RunAsync(root);
}
