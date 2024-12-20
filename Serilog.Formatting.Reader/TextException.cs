namespace Serilog.Formatting.Reader
{
    public class TextException : Exception
    {
        readonly string _text;

        public TextException(string text)
            : base("This exception type provides ToString() access to details only.")
        {
            _text = text;
        }

        public override string ToString()
        {
            return _text;
        }
    }
}
