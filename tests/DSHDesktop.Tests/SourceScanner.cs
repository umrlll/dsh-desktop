using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DSHDesktop.Tests;

/// <summary>
/// 结构测试共用的源码扫描器：定位仓库根、枚举被测工程源码，并提供"只剩代码"的投影
/// （抹掉注释与字符串/字符字面量）——判定位必须建立在投影上，否则注释里出现的
/// 标识符（例如一句"这里去掉 catch"）会把断言骗绿（该坑由变异验证逼出来，见 round-8 §6.3）。
/// </summary>
internal static class SourceScanner
{
    internal static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DSH.slnx"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "DSHDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "从 " + AppContext.BaseDirectory + " 向上找不到仓库根（同时含 DSH.slnx 与 src/DSHDesktop/）");
    }

    /// <summary>被测工程的全部手写源码；排除构建产物目录。</summary>
    internal static IEnumerable<string> ProductSources()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "DSHDesktop"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.Ordinal)
                        && !p.Contains(@"\.build\", StringComparison.Ordinal)
                        && !p.Contains(@"\runtime\", StringComparison.Ordinal)
                        && !p.Contains(@"\bin\", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);

    internal static string ProductFile(params string[] relativeParts)
        => Path.Combine(new[] { Path.Combine(RepoRoot, "src", "DSHDesktop") }.Concat(relativeParts).ToArray());

    /// <summary>只含代码的投影（注释与字面量替换成空格）。</summary>
    internal static string StrippedSource(string path) => StripCommentsAndStrings(File.ReadAllText(path));

    /// <summary>
    /// 把注释与字符串/字符字面量替换成空格，得到"只剩代码"的投影。
    /// 必要性（变异验证逼出来的）：只在原文里找 "catch" 会被注释里的字样骗过——
    /// 例如把 catch 删掉、只留一句"这里去掉 catch"的注释，测试仍然全绿。
    /// </summary>
    internal static string StripCommentsAndStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            int end;
            switch (c)
            {
                case '"':
                    end = SkipString(s, i);
                    break;
                case '\'':
                    end = SkipCharLiteral(s, i);
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '/':
                    end = SkipLineComment(s, i + 2);
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '*':
                    end = SkipBlockComment(s, i + 2);
                    break;
                default:
                    sb.Append(c);
                    continue;
            }

            sb.Append(' ', end - i + 1);
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>从声明处取出方法体（花括号配对；跳过字符串与注释里的花括号）。</summary>
    internal static string MethodBody(string source, int declarationIndex)
    {
        var open = source.IndexOf('{', declarationIndex);
        if (open < 0) throw new InvalidOperationException("声明之后找不到方法体起始花括号");
        var close = MatchingBrace(source, open);
        return source[(open + 1)..close];
    }

    /// <summary>从 openIndex 处的 '{' 找到配对的 '}'；跳过字符串/字符字面量与注释里的花括号。</summary>
    private static int MatchingBrace(string s, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '"':
                    i = SkipString(s, i);
                    break;
                case '\'':
                    i = SkipCharLiteral(s, i);
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '/':
                    i = SkipLineComment(s, i + 2);
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '*':
                    i = SkipBlockComment(s, i + 2);
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        throw new InvalidOperationException("花括号不配对：源码扫描器需要更新");
    }

    private static int SkipString(string s, int quoteIndex)
    {
        var verbatim = quoteIndex > 0 && s[quoteIndex - 1] == '@';
        for (var i = quoteIndex + 1; i < s.Length; i++)
        {
            if (verbatim)
            {
                if (s[i] != '"') continue;
                if (i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }   // "" 转义
                return i;
            }

            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"') return i;
        }
        throw new InvalidOperationException("字符串未闭合：源码扫描器需要更新");
    }

    private static int SkipCharLiteral(string s, int quoteIndex)
    {
        for (var i = quoteIndex + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '\'') return i;
        }
        throw new InvalidOperationException("字符字面量未闭合：源码扫描器需要更新");
    }

    private static int SkipLineComment(string s, int from)
    {
        for (var i = from; i < s.Length; i++)
            if (s[i] == '\n') return i;
        return s.Length - 1;
    }

    private static int SkipBlockComment(string s, int from)
    {
        for (var i = from; i + 1 < s.Length; i++)
            if (s[i] == '*' && s[i + 1] == '/') return i + 1;
        throw new InvalidOperationException("块注释未闭合：源码扫描器需要更新");
    }
}
