using System;
using System.Text.Json;

namespace LiteMonitor.Updater
{
    /// <summary>
    /// "可信摘要来源"：更新器在**调用方没有给出期望摘要**时，可以到哪里去取那份摘要。
    /// </summary>
    /// <remarks>
    /// ★ 这个接缝存在的唯一理由是**旧客户端兼容**（背景见
    ///   docs/plans/2026-10-23-updater-legacy-bridge-design.md）：
    ///   旧版主程序用单参数调用更新器（只给 ZIP 路径），而新版更新器要求必须带上期望 SHA-256；
    ///   旧客户端没有"把摘要交给更新器"的机会，于是只能让更新器自己去找那份可信摘要。
    /// ★ 默认实现是 <see cref="NullTrustedDigestSource"/>：永远取不到 ⇒ 单参数调用**仍然被拒绝**。
    ///   这是刻意的默认：没有可信摘要来源时，绝不允许"没给摘要也照样解压"。
    /// ★ 取值语义是 fail-closed 的：实现必须"拿不到就返回 null"，
    ///   绝不允许"取不到就返回一个占位摘要"，更不允许"取不到就放行"。
    /// ★ 本工程必须保持自包含（不能引用主程序工程），所以验签逻辑在这里独立实现一份；
    ///   它与主程序的实现是否漂移，由 LiteMonitor.Updater.Tests 里的对拍用例盯着。
    /// </remarks>
    internal interface ITrustedDigestSource
    {
        /// <summary>
        /// 尝试取得更新包的期望 SHA-256。
        /// </summary>
        /// <param name="zipFile">待更新的包路径（供实现按需定位相关元数据）。</param>
        /// <returns>
        /// 期望摘要（小写十六进制，64 位）。
        /// 取不到 / 来源不可信 / 发生任何异常 ⇒ <c>null</c>，调用方据此拒绝执行。
        /// </returns>
        string? TryGetExpectedDigest(string zipFile);
    }

    /// <summary>
    /// 默认来源：永远取不到摘要。
    /// </summary>
    /// <remarks>
    /// ★ 保持"单参数调用被拒绝"这一现状行为不变。没有装配具体来源时，
    ///   更新器必须拒绝没有摘要的调用，而不是"尽力而为"地放行。
    /// </remarks>
    internal sealed class NullTrustedDigestSource : ITrustedDigestSource
    {
        public string? TryGetExpectedDigest(string zipFile) => null;
    }

    /// <summary>
    /// 方案 (B) 的骨架：更新器自己取回 <c>version.json</c>，用**与旧客户端同一把**内嵌公钥
    /// 验签，从签名载荷里取出期望摘要。
    /// </summary>
    /// <remarks>
    /// ★ 信任级别与旧客户端**完全相同**：旧客户端也是"内嵌公钥验签 version.json → 取 sha256"。
    ///   差别只在于"谁去取 version.json"：旧客户端在主程序进程里取，更新器在自己进程里取。
    /// ★ 元数据获取方式是**注入**的（<c>Func&lt;string?&gt;</c>）。生产路径不注入任何实现，
    ///   因此**默认不联网**；是否开启抓取取决于发布策略（见设计说明 §5）。
    /// ★ 任何一步失败都返回 null（fail-closed）：取不到、解析不了、验不过、摘要形态非法，
    ///   一律等价于"没有可信摘要" ⇒ 调用方拒绝执行。
    /// </remarks>
    internal sealed class SignedVersionMetadataDigestSource : ITrustedDigestSource
    {
        /// <summary>
        /// 与旧客户端**同一把** ECDSA P-256 公钥（SubjectPublicKeyInfo / DER，单行 base64）。
        /// </summary>
        /// <remarks>
        /// ★ 这里只有公钥：公开它不构成任何额外能力。私钥只在发布流程里出现，绝不进版本库。
        /// ★ 换密钥会让所有用旧密钥签过的 version.json 立刻失效，
        ///   所以客户端与发布必须**同一次**更新，不能分开发布。
        /// ★ 两份拷贝（主程序 / 更新器）必须逐字节一致 —— 由 LiteMonitor.Updater.Tests
        ///   与 LiteMonitor.Tests 各自对着同一个指纹常量断言，漂移会立刻让测试变红。
        /// </remarks>
        internal const string UpdateSigningPublicKeySpkiBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAElF/yF1mc8EdJOeUhr/u5vxFJQXVm7oI1cEDuugjRmj5vWM9b14bZ5os1OZD9pDm0Gvj8GXevjHnl10G7kMeOuw==";

        /// <summary>
        /// 被签名的规范化载荷：<c>version\nsha256\nreleaseDate</c>（UTF-8，单个 <c>\n</c> 分隔）。
        /// </summary>
        /// <remarks>
        /// ★ 与主程序 <c>UpdateChecker.BuildUpdateSignaturePayload</c> 逐字节一致。
        ///   字段顺序就是协议；任何一方改了顺序/分隔符/字段，都必须同时改另一边，
        ///   否则会出现"发布端签得出来、客户端永远验不过"的静默失效。
        /// </remarks>
        internal static string BuildSignaturePayload(string version, string sha256, string releaseDate)
            => version + "\n" + sha256 + "\n" + releaseDate;

        private readonly Func<string?> _fetchVersionJson;
        private readonly string _publicKeySpkiBase64;

        /// <param name="fetchVersionJson">
        /// 取回 <c>version.json</c> 原文的委托。生产路径传 null ⇒ 本来源不可用（fail-closed）。
        /// </param>
        /// <param name="publicKeySpkiBase64">
        /// 验签用的公钥（SPKI base64）。为空时用内置的那把。
        /// </param>
        internal SignedVersionMetadataDigestSource(
            Func<string?>? fetchVersionJson,
            string? publicKeySpkiBase64 = null)
        {
            _fetchVersionJson = fetchVersionJson ?? (() => null);
            _publicKeySpkiBase64 = string.IsNullOrWhiteSpace(publicKeySpkiBase64)
                ? UpdateSigningPublicKeySpkiBase64
                : publicKeySpkiBase64!;
        }

        public string? TryGetExpectedDigest(string zipFile)
        {
            try
            {
                string? json = _fetchVersionJson();
                if (string.IsNullOrWhiteSpace(json)) return null;

                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string version = ReadString(root, "version");
                string sha256 = ReadString(root, "sha256");
                string releaseDate = ReadString(root, "releaseDate");
                string signature = ReadString(root, "signature");

                // 摘要形态先自检：不是 64 位十六进制就不必再费一次验签
                if (!Program.IsWellFormedDigest(sha256)) return null;

                if (!VerifySignature(version, sha256, releaseDate, signature, _publicKeySpkiBase64))
                    return null;

                return sha256.ToLowerInvariant();
            }
            catch
            {
                // 任何异常都等价于"拿不到可信摘要" ⇒ fail-closed
                return null;
            }
        }

        /// <summary>
        /// 用指定公钥校验元数据签名。
        /// </summary>
        private static bool VerifySignature(
            string version, string sha256, string releaseDate, string signature, string publicKeySpkiBase64)
        {
            if (string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(sha256) ||
                string.IsNullOrWhiteSpace(releaseDate) ||
                string.IsNullOrWhiteSpace(signature) ||
                string.IsNullOrWhiteSpace(publicKeySpkiBase64))
            {
                return false;
            }

            try
            {
                byte[] sig = Convert.FromBase64String(signature);
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                    BuildSignaturePayload(version, sha256, releaseDate));

                using var ecdsa = System.Security.Cryptography.ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(
                    Convert.FromBase64String(publicKeySpkiBase64), out _);

                return ecdsa.VerifyData(
                    payload,
                    sig,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element)) return string.Empty;
            return element.ValueKind == JsonValueKind.String
                ? (element.GetString() ?? string.Empty)
                : string.Empty;
        }
    }
}
