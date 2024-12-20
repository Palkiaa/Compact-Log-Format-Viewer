namespace Serilog.Formatting.Reader
{
    static class MessageTemplateSyntax
    {
        public static string Escape(string text)
        {
            return text.Replace("{", "{{").Replace("}", "}}");
        }
    }
}
