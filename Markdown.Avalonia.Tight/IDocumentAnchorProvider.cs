using ColorDocument.Avalonia;
using System;

namespace Markdown.Avalonia;

internal interface IDocumentAnchorProvider
{
    Action<string>? AnchorNavigationRequested { get; set; }

    bool TryGetDocumentAnchor(string anchor, out DocumentElement element);
}
