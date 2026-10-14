using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    /// <summary>
    /// The top document element.
    /// </summary>
    public class DocumentRootElement : DocumentElement
    {
        private Lazy<StackPanel> _block;
        private List<DocumentElement> _childrenList;
        private EnumerableEx<DocumentElement> _children;
        private SelectionList? _prevSelection;

        public override Control Control => _block.Value;
        public override IEnumerable<DocumentElement> Children => _children;

        /// <summary>
        /// 获取内部子元素列表，用于增量更新
        /// </summary>
        public List<DocumentElement> ChildrenList => _childrenList;

        /// <summary>
        /// 获取内部 StackPanel，用于增量更新
        /// </summary>
        public StackPanel Panel => _block.Value;

        /// <summary>
        /// 追加文档元素到当前文档
        /// </summary>
        /// <param name="elements">要追加的元素</param>
        public void AppendElements(IEnumerable<DocumentElement> elements)
        {
            // In virtualization mode the root StackPanel is intentionally never
            // materialized. The virtualizing panel is the sole visual owner of
            // child controls, while this root only tracks document semantics.
            var panel = _block.IsValueCreated ? _block.Value : null;
            foreach (var element in elements)
            {
                // 设置 Helper
                if (Helper != null)
                {
                    element.Helper = Helper;
                }

                // 添加到内部列表
                _childrenList.Add(element);

                // 添加到 Panel
                panel?.Children.Add(element.Control);
            }

            // 更新枚举器
            _children = _childrenList.ToEnumerable();
        }

        public DocumentRootElement(IEnumerable<DocumentElement> child)
        {
            _childrenList = child.ToList();
            _children = _childrenList.ToEnumerable();
            _block = new Lazy<StackPanel>(Create);
        }

        private StackPanel Create()
        {
            var panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;
            foreach (var child in _childrenList)
                panel.Children.Add(child.Control);

            return panel;
        }

        /// <summary>
        /// 使用新的文档元素进行增量更新
        /// </summary>
        /// <param name="newRoot">新解析的文档根元素</param>
        /// <returns>是否成功进行了增量更新</returns>
        public bool TryIncrementalUpdate(DocumentRootElement newRoot)
        {
            if (newRoot == null || !_block.IsValueCreated)
                return false;

            var newChildren = newRoot._childrenList;
            var panel = _block.Value;

            // 使用简化的差异算法：直接重建，但复用相同的元素
            ApplySimpleDiff(panel, newChildren);

            // 使哈希缓存失效
            InvalidateContentHash();

            return true;
        }

        /// <summary>
        /// 简化的差异应用算法：清空后重建，但复用相同哈希的元素
        /// 这种方法更稳健，虽然不是最优但足够高效
        /// </summary>
        private void ApplySimpleDiff(StackPanel panel, List<DocumentElement> newChildren)
        {
            // 创建旧元素的哈希映射，用于快速查找可复用的元素
            var oldElementsByHash = new Dictionary<string, List<DocumentElement>>();
            foreach (var oldChild in _childrenList)
            {
                var hash = oldChild.ContentHash;
                if (!oldElementsByHash.TryGetValue(hash, out var list))
                {
                    list = new List<DocumentElement>();
                    oldElementsByHash[hash] = list;
                }
                list.Add(oldChild);
            }

            // 构建新的子元素列表，尽可能复用旧元素
            var resultChildren = new List<DocumentElement>();
            var reusedElements = new HashSet<DocumentElement>();

            foreach (var newChild in newChildren)
            {
                var hash = newChild.ContentHash;
                DocumentElement? elementToUse = null;

                // 尝试找到可复用的旧元素
                if (oldElementsByHash.TryGetValue(hash, out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        if (!reusedElements.Contains(candidate) && candidate.CanReuseWith(newChild))
                        {
                            elementToUse = candidate;
                            reusedElements.Add(candidate);
                            break;
                        }
                    }
                }

                // 如果没有找到可复用的，使用新元素
                if (elementToUse == null)
                {
                    elementToUse = newChild;
                }

                // 设置 Helper
                if (Helper != null)
                {
                    elementToUse.Helper = Helper;
                }

                resultChildren.Add(elementToUse);
            }

            // 更新 Panel 的子元素
            // 先清空，再添加（这样更简单可靠）
            panel.Children.Clear();
            foreach (var child in resultChildren)
            {
                panel.Children.Add(child.Control);
            }

            // 更新内部列表
            _childrenList = resultChildren;
            _children = _childrenList.ToEnumerable();
        }

        public override void Select(Point from, Point to)
        {
            var selection = SelectionUtil.SelectVertical(Control, _children, from, to);

            if (_prevSelection is not null)
            {
                foreach (var ps in _prevSelection)
                {
                    if (!selection.Any(cs => ReferenceEquals(cs, ps)))
                    {
                        ps.UnSelect();
                    }
                }
            }

            _prevSelection = selection;
        }

        public override void UnSelect()
        {
            foreach (var child in _children)
                child.UnSelect();
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            if (_prevSelection is null)
                return;

            var preLen = builder.Length;

            foreach (var para in _prevSelection)
            {
                para.ConstructSelectedText(builder);

                if (preLen == builder.Length)
                    continue;

                if (builder[builder.Length - 1] != '\n')
                    builder.Append('\n');
            }
        }
    }
}
