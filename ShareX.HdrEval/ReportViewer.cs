#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Fullscreen image viewer embedded in the report.
    ///
    /// Comparing tonemap candidates means flipping between renderings of the same frame and looking
    /// at the same patch of pixels. A contact sheet cannot do that - the images are small and side by
    /// side, so the eye compares two things at once rather than one thing twice. Flipping in place at
    /// full size is how differences in a shoulder or a hue shift actually become visible.
    ///
    /// Left/right steps through the renderings of one frame; up/down moves between frames, holding
    /// the position in the set so you land on the same mode in the next frame.
    /// </summary>
    public static class ReportViewer
    {
        private sealed class ViewerItem
        {
            [JsonProperty("label")] public string Label { get; set; }
            [JsonProperty("src")] public string Src { get; set; }
            [JsonProperty("thumb")] public string Thumb { get; set; }
            [JsonProperty("note")] public string Note { get; set; }
        }

        private sealed class ViewerSet
        {
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("meta")] public string Meta { get; set; }
            [JsonProperty("items")] public List<ViewerItem> Items { get; set; }
        }

        /// <summary>Builds the per-frame image sets the viewer navigates.</summary>
        public static string BuildData(List<MetricRow> rows, EvalOptions options)
        {
            List<ViewerSet> sets = new List<ViewerSet>();

            foreach (var group in rows.GroupBy(r => r.FrameFile)
                .OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
            {
                List<MetricRow> frameRows = group.ToList();
                MetricRow first = frameRows[0];
                List<ViewerItem> items = new List<ViewerItem>();

                // The originals lead, so left/right always starts from the thing being matched.
                string hdr = frameRows.Select(r => r.ReferencePath).FirstOrDefault(p => p != null);
                string hdrThumb = frameRows.Select(r => r.ReferenceThumbPath).FirstOrDefault(p => p != null);
                if (hdr != null || hdrThumb != null)
                {
                    items.Add(new ViewerItem
                    {
                        Label = "HDR original",
                        Src = hdr ?? hdrThumb,
                        Thumb = hdrThumb ?? hdr,
                        Note = "PQ / BT.2020 - true HDR only on an HDR display"
                    });
                }

                string inRange = frameRows.Select(r => r.ReferenceInRangePath).FirstOrDefault(p => p != null);
                if (inRange != null)
                {
                    items.Add(new ViewerItem
                    {
                        Label = "original - in-range",
                        Src = inRange,
                        Thumb = inRange,
                        Note = "clipped at paper white"
                    });
                }

                string fullRange = frameRows.Select(r => r.ReferenceFullRangePath).FirstOrDefault(p => p != null);
                if (fullRange != null)
                {
                    items.Add(new ViewerItem
                    {
                        Label = "original - full range",
                        Src = fullRange,
                        Thumb = fullRange,
                        Note = "peak scaled to 1.0, nothing clips"
                    });
                }

                foreach (MetricRow row in frameRows
                    .Where(r => r.PngPath != null || r.ThumbPath != null)
                    .OrderBy(r => r.RequestedMode.ToString())
                    .ThenBy(r => r.Exposure))
                {
                    string label = row.RequestedMode.ToString();
                    if (row.ResolvedMode != row.RequestedMode)
                    {
                        label += " → " + row.ResolvedMode;
                    }

                    if (options.Exposures.Count > 1)
                    {
                        label += "  @" + row.Exposure.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    }

                    string note = row.Flags.Count > 0
                        ? string.Join("; ", row.Flags)
                        : "no flags";

                    items.Add(new ViewerItem
                    {
                        Label = label,
                        Src = row.PngPath ?? row.ThumbPath,
                        Thumb = row.ThumbPath ?? row.PngPath,
                        Note = note
                    });
                }

                if (items.Count == 0)
                {
                    continue;
                }

                sets.Add(new ViewerSet
                {
                    Title = first.Label ?? first.FrameFile,
                    Meta = $"{first.Scenario} · {first.Width}×{first.Height} · peak " +
                        $"{first.RefPeakNits.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} nits",
                    Items = items
                });
            }

            return JsonConvert.SerializeObject(sets);
        }

        public static void AppendStyles(StringBuilder html)
        {
            html.AppendLine(@"
#viewer { position: fixed; inset: 0; background: #000; z-index: 9999; display: none; }
#viewer.open { display: block; }
#viewer img#viewerImage { position: absolute; inset: 0; margin: auto;
  max-width: 100%; max-height: 100%; object-fit: contain; }
#viewer .chrome { transition: opacity .35s ease; opacity: 1; }
#viewer.idle .chrome { opacity: 0; pointer-events: none; }
#viewerInfo { position: absolute; top: 0; left: 0; padding: 14px 18px;
  background: linear-gradient(to bottom right, rgba(0,0,0,.72), transparent);
  color: #fff; font: 600 15px/1.4 -apple-system, 'Segoe UI', Roboto, sans-serif;
  text-shadow: 0 1px 3px rgba(0,0,0,.9); max-width: 70vw; }
#viewerInfo .sub { font-weight: 400; font-size: 12.5px; opacity: .85; margin-top: 3px; }
#viewerInfo .note { font-weight: 400; font-size: 12px; opacity: .8; margin-top: 5px; color: #ffb4a8; }
#viewerCount { position: absolute; top: 0; right: 0; padding: 14px 18px; color: #fff;
  font: 400 12.5px/1.4 ui-monospace, Consolas, monospace; opacity: .8;
  text-shadow: 0 1px 3px rgba(0,0,0,.9); text-align: right; }
#viewerStrip { position: absolute; left: 0; right: 0; bottom: 0; padding: 10px 12px 12px;
  background: linear-gradient(to top, rgba(0,0,0,.85), rgba(0,0,0,0));
  display: flex; gap: 8px; justify-content: center; align-items: flex-end;
  overflow-x: auto; }
#viewerStrip figure { margin: 0; flex: 0 0 auto; width: 132px; cursor: pointer; opacity: .55;
  transition: opacity .15s ease; }
#viewerStrip figure:hover { opacity: .85; }
#viewerStrip figure.active { opacity: 1; }
#viewerStrip img { display: block; width: 100%; height: 74px; object-fit: cover;
  border: 2px solid transparent; border-radius: 3px; background: #111; }
#viewerStrip figure.active img { border-color: #fff; }
#viewerStrip figcaption { color: #fff; font: 500 11px/1.3 -apple-system, 'Segoe UI', sans-serif;
  margin-top: 4px; text-align: center; white-space: nowrap; overflow: hidden;
  text-overflow: ellipsis; text-shadow: 0 1px 2px rgba(0,0,0,.9); }
#viewerHelp { position: absolute; bottom: 0; left: 0; padding: 0 0 128px 18px; color: #fff;
  font: 400 12px/1.6 -apple-system, 'Segoe UI', sans-serif; opacity: .7;
  text-shadow: 0 1px 3px rgba(0,0,0,.9); }
#viewerHelp kbd { background: rgba(255,255,255,.16); border-radius: 3px; padding: 1px 5px;
  font: inherit; font-family: ui-monospace, Consolas, monospace; }
.shot a.viewerLink { cursor: zoom-in; display: block; }
");
        }

        public static void AppendMarkup(StringBuilder html, string dataJson)
        {
            html.AppendLine(@"<div id=""viewer"" tabindex=""-1"">
  <img id=""viewerImage"" alt="""">
  <div id=""viewerInfo"" class=""chrome""></div>
  <div id=""viewerCount"" class=""chrome""></div>
  <div id=""viewerHelp"" class=""chrome"">
    <kbd>&larr;</kbd><kbd>&rarr;</kbd> rendering &nbsp; <kbd>&uarr;</kbd><kbd>&darr;</kbd> frame &nbsp;
    <kbd>F</kbd> fullscreen &nbsp; <kbd>Esc</kbd> close
  </div>
  <div id=""viewerStrip"" class=""chrome""></div>
</div>");

            html.AppendLine("<script id=\"viewerData\" type=\"application/json\">");
            html.AppendLine(dataJson);
            html.AppendLine("</script>");
            html.AppendLine(Script);
        }

        private const string Script = @"<script>
(function () {
  var SETS = JSON.parse(document.getElementById('viewerData').textContent);
  if (!SETS.length) return;

  var root = document.getElementById('viewer');
  var img = document.getElementById('viewerImage');
  var info = document.getElementById('viewerInfo');
  var count = document.getElementById('viewerCount');
  var strip = document.getElementById('viewerStrip');
  var si = 0, ii = 0, idleTimer = null;
  var IDLE_MS = 5000;

  // Chrome hides on a timer that only mouse movement resets. Keyboard navigation deliberately
  // leaves it hidden: once you are flipping between renderings, an overlay redrawing next to the
  // pixels you are judging is the last thing you want.
  function wake() {
    root.classList.remove('idle');
    if (idleTimer) clearTimeout(idleTimer);
    idleTimer = setTimeout(function () { root.classList.add('idle'); }, IDLE_MS);
  }

  function preload(set) {
    set.items.forEach(function (it) { var p = new Image(); p.src = it.src; });
  }

  function buildStrip() {
    var set = SETS[si];
    strip.textContent = '';
    set.items.forEach(function (it, k) {
      var fig = document.createElement('figure');
      if (k === ii) fig.className = 'active';
      var im = document.createElement('img');
      im.src = it.thumb || it.src;
      im.loading = 'lazy';
      im.alt = it.label;
      var cap = document.createElement('figcaption');
      cap.textContent = it.label;
      fig.appendChild(im);
      fig.appendChild(cap);
      fig.addEventListener('click', function () { ii = k; render(); wake(); });
      strip.appendChild(fig);
    });
  }

  function render(rebuildStrip) {
    var set = SETS[si];
    if (ii >= set.items.length) ii = set.items.length - 1;
    var item = set.items[ii];

    img.src = item.src;
    img.alt = item.label;

    info.innerHTML = '';
    var t = document.createElement('div');
    t.textContent = item.label;
    info.appendChild(t);
    var s = document.createElement('div');
    s.className = 'sub';
    s.textContent = set.title + '  ·  ' + set.meta;
    info.appendChild(s);
    if (item.note && item.note !== 'no flags') {
      var n = document.createElement('div');
      n.className = 'note';
      n.textContent = item.note;
      info.appendChild(n);
    }

    count.textContent = 'frame ' + (si + 1) + ' / ' + SETS.length +
      '\n' + (ii + 1) + ' / ' + set.items.length;
    count.style.whiteSpace = 'pre';

    if (rebuildStrip !== false) buildStrip();
    else {
      Array.prototype.forEach.call(strip.children, function (c, k) {
        c.className = k === ii ? 'active' : '';
      });
    }
  }

  function open(setIndex, itemIndex) {
    si = Math.max(0, Math.min(setIndex || 0, SETS.length - 1));
    ii = Math.max(0, itemIndex || 0);
    root.classList.add('open');
    render();
    preload(SETS[si]);
    wake();
    root.focus();
    if (root.requestFullscreen) { root.requestFullscreen().catch(function () {}); }
    document.body.style.overflow = 'hidden';
  }

  function close() {
    root.classList.remove('open');
    document.body.style.overflow = '';
    if (idleTimer) clearTimeout(idleTimer);
    if (document.fullscreenElement && document.exitFullscreen) {
      document.exitFullscreen().catch(function () {});
    }
  }

  document.addEventListener('keydown', function (e) {
    if (!root.classList.contains('open')) return;
    var set = SETS[si];
    var handled = true;

    if (e.key === 'ArrowRight') { ii = (ii + 1) % set.items.length; render(false); }
    else if (e.key === 'ArrowLeft') { ii = (ii - 1 + set.items.length) % set.items.length; render(false); }
    else if (e.key === 'ArrowDown') { si = (si + 1) % SETS.length; render(); preload(SETS[si]); }
    else if (e.key === 'ArrowUp') { si = (si - 1 + SETS.length) % SETS.length; render(); preload(SETS[si]); }
    else if (e.key === 'Escape') { close(); }
    else if (e.key === 'f' || e.key === 'F') {
      if (document.fullscreenElement) { document.exitFullscreen(); }
      else if (root.requestFullscreen) { root.requestFullscreen().catch(function () {}); }
    }
    else { handled = false; }

    if (handled) e.preventDefault();
    // Deliberately no wake() here: keys navigate, only the mouse reveals chrome.
  });

  root.addEventListener('mousemove', wake);
  root.addEventListener('click', function (e) {
    if (e.target === root || e.target === img) close();
  });

  document.addEventListener('click', function (e) {
    var link = e.target.closest ? e.target.closest('a.viewerLink') : null;
    if (!link) return;
    e.preventDefault();
    open(parseInt(link.getAttribute('data-set'), 10), parseInt(link.getAttribute('data-item'), 10));
  });
})();
</script>";
    }
}
