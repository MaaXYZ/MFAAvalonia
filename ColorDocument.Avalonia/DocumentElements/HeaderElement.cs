using ColorTextBlock.Avalonia;
using System.Collections.Generic;

namespace ColorDocument.Avalonia.DocumentElements
{
    public class HeaderElement : CTextBlockElement, IDocumentHeading
    {
        private readonly global::Avalonia.Controls.Border? _container;

        public int Level { get; }

        public override global::Avalonia.Controls.Control Control => _container ?? base.Control;

        public HeaderElement(IEnumerable<CInline> inlines, int level) :
            base(inlines, level switch
            {
                1 => ClassNames.Heading1Class,
                2 => ClassNames.Heading2Class,
                3 => ClassNames.Heading3Class,
                4 => ClassNames.Heading4Class,
                5 => ClassNames.Heading5Class,
                _ => ClassNames.Heading6Class,
            })
        {
            Level = level switch
            {
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 5,
                _ => 6,
            };

            if (Level <= 2)
            {
                _container = new global::Avalonia.Controls.Border { Child = base.Control };
                _container.Classes.Add(Level == 1 ? "Heading1Container" : "Heading2Container");
            }
        }
    }
}
