using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClipShelf;

internal sealed class DocxPaginationState
{
    internal readonly List<PreviewScene> Pages = new();
    internal readonly Dictionary<string, int> Counters = new();
    internal int BodyIndex, BlockIndex, TextOffset, TableRow;
    internal DocBlock[] Blocks = Array.Empty<DocBlock>();
    internal bool Complete;
}
internal sealed class DocxPaginator(DocxDocumentModel model, DocxPaginationState state)
{
    internal static SceneRun[] Slice(SceneRun[] source, int start, int length = int.MaxValue) {
        var result = new List<SceneRun>();
        foreach (var run in source) { if (start >= run.Text.Length) { start -= run.Text.Length; continue; } int count = Math.Min(length, run.Text.Length - start); if (count > 0) result.Add(run with { Text = run.Text.Substring(start, count) }); length -= count; start = 0; if (length == 0) break; }
        return result.ToArray();
    }
    internal void Next(CancellationToken token) {
        using var timing = PreviewMetrics.Measure("docx-pagination");
        if (state.Complete) return; if (state.Pages.Count >= 1000) throw SafeOoxmlPackage.Limit();
        int savedBody = state.BodyIndex, savedBlock = state.BlockIndex, savedOffset = state.TextOffset, savedRow = state.TableRow;
        var savedBlocks = state.Blocks; var savedCounters = state.Counters.ToArray();
        try {
            var nodes = new List<SceneNode>(); double y = model.Top, bottom = model.Height - model.Bottom, bodyWidth = model.Width - model.Left - model.Right;
            bool finishedPage = false;
            void DoneBlock() { state.BlockIndex++; state.TextOffset = state.TableRow = 0; }
            while (!finishedPage) {
                token.ThrowIfCancellationRequested();
                if (state.BlockIndex >= state.Blocks.Length) {
                    if (state.BodyIndex >= model.Body.Length) { state.Complete = true; break; }
                    state.Blocks = model.Parse(state.BodyIndex++, token); state.BlockIndex = 0; continue;
                }
                switch (state.Blocks[state.BlockIndex]) {
                    case DocPageBreak: DoneBlock(); if (nodes.Count > 0) finishedPage = true; break;
                    case DocParagraph p:
                        var all = string.Concat(p.Runs.Select(r => r.Text));
                        if (state.TextOffset == 0) { if (nodes.Count > 0 && y + p.Before + (p.KeepNext ? 60 : 30) > bottom) { finishedPage = true; break; } y += Math.Clamp(p.Before, 0, 300); }
                        if (all.Length == 0) { y += 20; DoneBlock(); break; }
                        if (state.TextOffset >= all.Length) { y += Math.Clamp(p.After, 0, 300); DoneBlock(); break; }
                        double left = Math.Clamp(p.Left + (state.TextOffset == 0 ? p.First : 0), -model.Left + 4, bodyWidth - 20);
                        double width = Math.Max(20, bodyWidth - left - Math.Clamp(p.Right, 0, bodyWidth / 2));
                        int available = Math.Min(512, all.Length - state.TextOffset); int newline = all.IndexOf('\n', state.TextOffset, available);
                        if (newline >= 0) available = newline - state.TextOffset;
                        int low = 1, high = Math.Max(1, available), count = 1;
                        while (low <= high) { token.ThrowIfCancellationRequested(); int mid = (low + high) / 2; var measure = PreviewSceneRenderer.Text(Slice(p.Runs, state.TextOffset, mid), 100000);
                            if (measure.WidthIncludingTrailingWhitespace <= width) { count = mid; low = mid + 1; } else high = mid - 1; }
                        count = Math.Min(count, Math.Max(1, available));
                        if (count < available) { int space = all.LastIndexOf(' ', state.TextOffset + count - 1, count); if (space > state.TextOffset + count / 2) count = space - state.TextOffset + 1; }
                        if (count > 1 && char.IsHighSurrogate(all[Math.Min(all.Length - 1, state.TextOffset + count - 1)])) count--;
                        if (count == 1 && state.TextOffset + 1 < all.Length && char.IsSurrogatePair(all, state.TextOffset)) count = 2;
                        var runs = available == 0 ? new[] { new SceneRun(" ") } : Slice(p.Runs, state.TextOffset, count);
                        var line = PreviewSceneRenderer.Text(runs, width); double lineHeight = p.LineHeight > 0 ? p.LineHeight : line.Height * 1.08;
                        if (y + lineHeight > bottom && nodes.Count > 0) { finishedPage = true; break; }
                        nodes.Add(new SceneText(new(model.Left + left, y, width, Math.Max(line.Height, lineHeight)), runs, p.Align));
                        y += lineHeight; state.TextOffset += available == 0 ? 1 : count;
                        if (state.TextOffset == newline) state.TextOffset++;
                        break;
                    case DocImage image:
                        double scale = Math.Min(1, Math.Min(bodyWidth / image.Width, (bottom - model.Top) / image.Height)); double ih = image.Height * scale;
                        if (y + ih > bottom && nodes.Count > 0) { finishedPage = true; break; }
                        nodes.Add(new SceneImage(new(model.Left, y, image.Width * scale, ih), image.Bytes)); y += ih + 8; DoneBlock(); break;
                    case DocTable table:
                        if (state.TableRow >= table.Rows.Length) { DoneBlock(); break; }
                        var row = table.Rows[state.TableRow]; double cw = bodyWidth / Math.Max(1, table.Columns);
                        double rh = Math.Max(28, row.Select(c => PreviewSceneRenderer.Text(c.Runs, Math.Max(1, cw - 12)).Height + 12).DefaultIfEmpty(28).Max());
                        if (rh > bottom - model.Top) { model.Warnings.Add("超高表格行仅显示页内部分"); rh = bottom - model.Top; }
                        if (y + rh > bottom && nodes.Count > 0) { finishedPage = true; break; }
                        for (int col = 0; col < row.Length; col++) { nodes.Add(new SceneShape(new(model.Left + col * cw, y, cw, rh), "rect", "#FFFFFF", "#B0B7C2")); nodes.Add(new SceneText(new(model.Left + col * cw + 6, y + 6, Math.Max(1, cw - 12), rh - 12), row[col].Runs)); }
                        y += rh; state.TableRow++; break;
                    case DocPlaceholder placeholder:
                        if (y + placeholder.Height > bottom && nodes.Count > 0) { finishedPage = true; break; }
                        nodes.Add(new ScenePlaceholder(new(model.Left, y, bodyWidth, placeholder.Height), placeholder.Kind)); y += placeholder.Height + 8; DoneBlock(); break;
                }
                if (nodes.Count > 5000) throw SafeOoxmlPackage.Limit();
            }
            token.ThrowIfCancellationRequested();
            if (nodes.Count > 0 || state.Pages.Count == 0) {
                if (model.Header.Length > 0) nodes.Add(new SceneText(new(model.Left, 20, bodyWidth, Math.Max(20, model.Top - 30)), model.Header));
                if (model.Footer.Length > 0) nodes.Add(new SceneText(new(model.Left, bottom + 20, bodyWidth, Math.Max(20, model.Bottom - 30)), model.Footer));
                state.Pages.Add(new(model.Width, model.Height, "#FFFFFF", nodes.ToArray(), model.Warnings.Append("近似排版，非 Office 精确分页").Distinct().ToArray()));
            }
        } catch {
            state.BodyIndex = savedBody; state.BlockIndex = savedBlock; state.TextOffset = savedOffset; state.TableRow = savedRow; state.Blocks = savedBlocks; state.Complete = false;
            state.Counters.Clear(); foreach (var pair in savedCounters) state.Counters[pair.Key] = pair.Value; throw;
        }
    }
}
