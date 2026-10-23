using System.Runtime.CompilerServices;

// ★ 探针的内部可见性出口。
//   LiteMonitor.Tests —— 单元测试工程（LM-086 / LM-087 / LM-088 的回归护栏）。
//
//   为什么需要：探针里的"隔离门判定"（IsolationGate）、"持续送帧通道"（ScenePump）、
//   "源码漂移比对"（CandidateHashGuard）都是 **internal** —— 它们是探针的内部机制，
//   不是对外公开的产品 API。护栏用例必须能**真的调用**这些内部逻辑，
//   否则就只能退化成"读源码文本"的弱断言（改个变量名就失效）。
//
//   ★ 与 src/UI、src/Rendering 的做法一致：生产代码里的测试钩子一律收成 internal，
//   不扩大对外公开面（见 LiteMonitor.csproj 的同名 ItemGroup）。
[assembly: InternalsVisibleTo("LiteMonitor.Tests")]
