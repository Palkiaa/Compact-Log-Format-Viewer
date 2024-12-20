using Serilog.Events;

namespace Serilog.Formatting.Reader
{
    class RenderableScalarValue : ScalarValue
    {
        readonly Dictionary<string, string> _renderings = new();

        public RenderableScalarValue(object? value, IEnumerable<Rendering> renderings)
            : base(value)
        {
            if (renderings == null) throw new ArgumentNullException(nameof(renderings));
            foreach (var rendering in renderings)
                _renderings[rendering.Format] = rendering.Rendered;
        }

        public override void Render(TextWriter output, string? format = null, IFormatProvider? formatProvider = null)
        {
            if (format != null && _renderings.TryGetValue(format, out var rendering))
                output.Write(rendering);
            else
                base.Render(output, format, formatProvider);
        }
    }
}
