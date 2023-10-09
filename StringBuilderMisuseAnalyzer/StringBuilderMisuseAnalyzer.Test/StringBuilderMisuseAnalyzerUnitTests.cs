using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

using VerifyCS = StringBuilderMisuseAnalyzer.Test.CSharpCodeFixVerifier<
    StringBuilderMisuseAnalyzer.StringBuilderMisuseAnalyzer,
    StringBuilderMisuseAnalyzer.StringBuilderMisuseCodeFixer>;

namespace StringBuilderMisuseAnalyzer.Test
{
    [TestClass]
    public class StringBuilderMisuseAnalyzerUnitTest
    {
        [TestMethod]
        public async Task AppendAndAppendLineAsync()
        {
            await VerifyCS.VerifyCodeFixAsync("""
                using System.Text;

                static class Program
                {
                    static void Main()
                    {
                        var {|MBSBM01:sb|} = new StringBuilder();
                        sb.Append("meep-");
                        sb.AppendLine("moop");
                        sb.Append("maap");

                        var res = sb.ToString();
                    }
                }
                """, """"
                using System.Text;
                
                static class Program
                {
                    static void Main()
                    {

                        var res = """
                            meep-moop
                            maap
                            """;
                    }
                }
                """");
        }

        [TestMethod]
        public async Task AppendFormatAsync()
        {
            await VerifyCS.VerifyCodeFixAsync("""
                using System.Text;
                
                static class Program
                {
                    static void Main()
                    {
                        var x = 2;
                        var y = 122;
                        var s = $"{x + y}";
                
                        var {|MBSBM01:sb|} = new StringBuilder();
                        sb.Append("meep-");
                        sb.AppendFormat("moop {0} + {2} = {1} marf!", x, s, y);
                        sb.AppendLine();
                        sb.Append("maap");
                
                        var res = sb.ToString();
                    }
                }
                """, """"
                using System.Text;
                
                static class Program
                {
                    static void Main()
                    {
                        var x = 2;
                        var y = 122;
                        var s = $"{x + y}";
                
                        var res = $$"""
                            meep-moop {{x}} + {{y}} = {{s}} marf!
                            maap
                            """;
                    }
                }
                """");
        }
    }
}
