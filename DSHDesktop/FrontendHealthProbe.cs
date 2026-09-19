using System.Text.Json;

namespace DSHDesktop;

/// <summary>
/// Browser-side probe used after a successful main-document navigation. A loaded HTML document is
/// not considered healthy until the DSH root contains visible, meaningful UI.
/// </summary>
internal static class FrontendHealthProbe
{
    internal enum Result
    {
        Healthy,
        Loading,
        MissingRoot,
        EmptySurface,
        Invalid,
    }

    // Keep this self-contained: ExecuteScriptAsync evaluates it in the current main document and
    // returns the final string as JSON. The scan is bounded so a hostile/broken plugin cannot make
    // one health check walk an unbounded DOM.
    internal const string Script = """
        (() => {
          if (document.readyState !== 'complete') return 'loading';
          const root = document.getElementById('root');
          if (!root) return 'missing-root';
          const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
          let element = walker.currentNode;
          let visited = 0;
          while (element && visited < 2048) {
            visited += 1;
            const meaningful = element.matches('button,input,textarea,select,img,svg,canvas,[contenteditable="true"]')
              || Array.from(element.childNodes).some(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim());
            if (meaningful) {
              const style = getComputedStyle(element);
              const rect = element.getBoundingClientRect();
              if (style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity) !== 0
                && rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.right > 0
                && rect.top < innerHeight && rect.left < innerWidth) return 'ready';
            }
            element = walker.nextNode();
          }
          return element ? 'loading' : 'empty-surface';
        })()
        """;

    internal static Result Parse(string? executeScriptJson)
    {
        if (string.IsNullOrWhiteSpace(executeScriptJson)) return Result.Invalid;
        try
        {
            return JsonSerializer.Deserialize<string>(executeScriptJson) switch
            {
                "ready" => Result.Healthy,
                "loading" => Result.Loading,
                "missing-root" => Result.MissingRoot,
                "empty-surface" => Result.EmptySurface,
                _ => Result.Invalid,
            };
        }
        catch (JsonException)
        {
            return Result.Invalid;
        }
    }

    internal static string Describe(Result result) => result switch
    {
        Result.Loading => "页面仍在加载",
        Result.MissingRoot => "页面缺少 #root 容器",
        Result.EmptySurface => "页面没有可见的交互内容",
        Result.Invalid => "页面健康探测返回无效结果",
        _ => "页面已就绪",
    };
}
