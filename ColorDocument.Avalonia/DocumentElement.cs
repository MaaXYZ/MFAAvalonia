using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ColorDocument.Avalonia
{
    public abstract class DocumentElement
    {
        private ISelectionRenderHelper? _helper;
        private string? _cachedContentHash;

        public abstract Control Control { get; }
        public abstract IEnumerable<DocumentElement> Children { get; }

        /// <summary>
        /// Releases visual state that can be recreated from the semantic model.
        /// Most document elements keep their existing behavior; deferred elements
        /// override this to keep virtualization memory bounded.
        /// </summary>
        public virtual void ReleaseControl()
        {
        }

        /// <summary>
        /// 获取元素类型标识，用于增量更新时的类型比较
        /// </summary>
        public virtual string ElementType => GetType().Name;

        /// <summary>
        /// 获取元素内容的哈希值，用于增量更新时判断内容是否变化
        /// </summary>
        public string ContentHash
        {
            get
            {
                if (_cachedContentHash == null)
                {
                    _cachedContentHash = ComputeContentHash();
                }
                return _cachedContentHash;
            }
        }

        /// <summary>
        /// 计算内容哈希值，子类可重写以提供更精确的哈希计算
        /// </summary>
        protected virtual string ComputeContentHash()
        {
            var sb = new StringBuilder();
            sb.Append(ElementType);
            sb.Append(':');
            BuildContentString(sb);
            
            // 使用简单的哈希算法
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash, 0, 12); // 只取前12字节，足够区分
        }

        /// <summary>
        /// 构建用于哈希计算的内容字符串，子类应重写此方法
        /// </summary>
        protected virtual void BuildContentString(StringBuilder sb)
        {
            // 默认实现：使用子元素的哈希
            foreach (var child in Children)
            {
                sb.Append(child.ContentHash);
                sb.Append('|');
            }
        }

        /// <summary>
        /// 使缓存的哈希值失效，当内容变化时调用
        /// </summary>
        protected void InvalidateContentHash()
        {
            _cachedContentHash = null;
        }

        /// <summary>
        /// 判断是否可以与另一个元素进行增量更新复用
        /// </summary>
        public virtual bool CanReuseWith(DocumentElement other)
        {
            return other != null
                && ElementType == other.ElementType
                && ContentHash == other.ContentHash;
        }

        public ISelectionRenderHelper? Helper
        {
            get => _helper;
            set
            {
                _helper = value;
                foreach (var child in Children)
                    child.Helper = value;
            }
        }

        public Rect GetRect(Layoutable anchor) => Control.GetRectInDoc(anchor).GetValueOrDefault();
        public abstract void Select(Point from, Point to);
        public abstract void UnSelect();

        public virtual string GetSelectedText()
        {
            var builder = new StringBuilder();
            ConstructSelectedText(builder);
            return builder.ToString();
        }

        public abstract void ConstructSelectedText(StringBuilder stringBuilder);

    }

    public interface ISelectionRenderHelper
    {
        void Register(Control control);
        void Unregister(Control control);
    }
}
