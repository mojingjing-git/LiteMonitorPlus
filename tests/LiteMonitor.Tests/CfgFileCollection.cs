using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 串行化"会读写 settings.json"的测试类。
    ///
    /// 原因: <c>SettingsHelper.FilePath</c> 固定指向
    /// <c>AppContext.BaseDirectory\settings.json</c>, 同一个测试程序集里两个调
    /// <c>Load()</c> / <c>Save()</c> 的用例会**并行访问同一个文件**,
    /// 实测抛 <c>IOException: The process cannot access the file ... because it is used by another process</c>。
    ///
    /// 用法: 给相关测试类加 <c>[Collection("cfgfile")]</c>。
    /// </summary>
    [CollectionDefinition("cfgfile", DisableParallelization = true)]
    public class CfgFileCollection : ICollectionFixture<object>
    {
    }
}
