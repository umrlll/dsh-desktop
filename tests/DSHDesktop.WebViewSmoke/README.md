# DSHDesktop WebView2 smoke

This opt-in Windows smoke starts a real WebView2 controller against an ephemeral exact-loopback
HTTP server. It passes only when navigation stays on the owned origin and the production
`FrontendHealthProbe` observes a visible interactive `#root` surface.

```powershell
dotnet run --project tests\DSHDesktop.WebViewSmoke\DSHDesktop.WebViewSmoke.csproj -c Release
```

The smoke deliberately stays outside the ordinary unit-test project: it requires Windows,
WebView2 Evergreen Runtime, and an interactive desktop session. Release CI should run it on the
same Windows image used for installer acceptance.
