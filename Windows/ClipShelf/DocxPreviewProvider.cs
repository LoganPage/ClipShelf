using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace ClipShelf;
internal sealed class DocxPreviewProvider(PreviewCacheService cache) : OoxmlPreviewProvider(cache)
{
    private DocxPaginationState state = null!; private DocxPaginator paginator = null!;
    public override bool CanPreview(string file) => PreviewFormatRegistry.Get(file) == PreviewFormat.Word;
    protected override void ReadInfo(CancellationToken token) {
        state = Cache.Model<DocxPaginationState>(Identity.Id + ":docx-checkpoints") ?? new();
        var model = new DocxDocumentModel(Package, Cache, Identity.Id, state.Counters, token); paginator = new(model, state);
        Info = new(Identity.Id, "DOCX", System.IO.Path.GetFileName(Identity.Path), state.Complete ? state.Pages.Count : null, model.Width, model.Height, Package.Has("docProps/thumbnail.jpeg"), PreviewProviderRegistry.Version);
    }
    private void Step(CancellationToken token) {
        paginator.Next(token); Info = Info with { PageCount = state.Complete ? state.Pages.Count : null };
        // Conservative accounting: scene nodes, text and package media are bounded separately.
        long cost = state.Pages.Sum(p => p.Nodes.Length * 128L + p.Nodes.OfType<SceneText>().Sum(t => t.Runs.Sum(r => r.Text.Length * 2L)))
            + state.Pages.SelectMany(p => p.Nodes.OfType<SceneImage>()).Select(i => i.Bytes).Distinct().Sum(bytes => bytes.LongLength);
        Cache.Model(Identity.Id + ":docx-checkpoints", state, cost + 4096);
        if (cost > PreviewCacheService.MemoryLimit) throw SafeOoxmlPackage.Limit();
    }
    protected override async Task<PreviewScene> GetSceneAsync(int page, CancellationToken token) {
        while (state.Pages.Count <= page && !state.Complete) await Cache.Scheduler.Run(() => { Step(token); return true; }, token);
        return state.Pages[Math.Clamp(page, 0, state.Pages.Count - 1)];
    }
    public override async Task<int?> GetPageCountAsync(bool complete, CancellationToken token) {
        while (complete && !state.Complete) { await Cache.Scheduler.Run(() => { Step(token); return true; }, token, 2); await Task.Delay(1, token); }
        return state.Complete ? state.Pages.Count : null;
    }
}
