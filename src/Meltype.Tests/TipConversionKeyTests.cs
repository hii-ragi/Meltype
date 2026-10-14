// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Text.Json;
using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;
using Meltype.Tip;

namespace Meltype.Tests;

/// <summary>DLLから届く既存のkey要求を、実際のTipServer・セッションに通す。</summary>
internal static class TipConversionKeyTests
{
    private sealed class Server : IDisposable
    {
        public readonly Settings Settings = new() { Mode = InputMode.Tsf };
        private readonly Control _invoker = new();
        private readonly CompositionService _composition;
        private readonly TipServer _server;
        private readonly Dictionary<string, MeltypeSession> _sessions = [];
        private static readonly MethodInfo Handle = typeof(TipServer).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public Server()
        {
            _composition = new(_invoker, CompositionDetector.CreateDefault(), new CompositionOptions
            {
                UserDictionary = new(null), History = new(null), Languages = new(null),
                TranslationHistory = new(null), Predictor = new(new PhraseHistory(null), new UserDictionary(null), new ConversionHistory(null)),
                Engine = () => ConversionEngine.System,
            });
            _server = new(_invoker, _composition, () => Settings);
        }

        public MeltypeSession Session => _sessions["conversion-test"];

        public JsonDocument Cancel() => JsonDocument.Parse((string)Handle.Invoke(_server,
            ["{\"op\":\"commit\",\"sid\":\"conversion-test\",\"cancelReconversion\":true}", _sessions])!);

        public JsonDocument Send(int vk, object? selection = null, string? selectedText = null, bool composing = false, int mods = 0, char? ch = null, bool code = true, bool unknownSelection = false)
        {
            var request = new Dictionary<string, object?>
            {
                ["op"] = "key", ["sid"] = "conversion-test", ["process"] = "notepad", ["code"] = code,
                ["vk"] = vk, ["mods"] = mods, ["ch"] = ch is { } c ? (int)c : 0, ["composing"] = composing,
            };
            if (selection is not null) request["selection"] = selection;
            if (unknownSelection) request["selection"] = null;
            if (selectedText is not null) request["selectedText"] = selectedText;
            return JsonDocument.Parse((string)Handle.Invoke(_server, [JsonSerializer.Serialize(request), _sessions])!);
        }

        public void Dispose() { _server.Dispose(); _composition.Dispose(); _invoker.Dispose(); }
    }

    [Test]
    public static void TipConversionKeys_NoSelectionSetsJapaneseAndDirect()
    {
        using var server = new Server();
        foreach (var vk in new[] { VirtualKeys.NonConvert, VirtualKeys.Convert, VirtualKeys.Convert, VirtualKeys.NonConvert })
        {
            using var reply = server.Send(vk, selection: false);
            var root = reply.RootElement;
            Assert.Equal(vk == VirtualKeys.Convert, root.GetProperty("inputMode").GetBoolean());
            Assert.True(root.GetProperty("consumed").GetBoolean(), "切り替えキーをアプリに渡さない");
            Assert.Equal(0, root.GetProperty("commits").GetArrayLength());
        }
    }

    [Test]
    public static void TipConversionKeys_DirectToJapaneseAlsoEnablesCodeLine()
    {
        using var server = new Server();
        using var direct = server.Send(VirtualKeys.NonConvert, false);
        server.Session.CodeInput = true;
        using var japanese = server.Send(VirtualKeys.Convert, false, code: false);
        Assert.True(japanese.RootElement.GetProperty("inputMode").GetBoolean(), "英数から日本語へ");
        Assert.True(!server.Session.CodeEnglishAt("var x = "), "コードの行も日本語にする");
        using var directAgain = server.Send(VirtualKeys.NonConvert, false, code: false);
        Assert.True(server.Session.Direct, "無変換で英数へ");
        using var japaneseAgain = server.Send(VirtualKeys.Convert, false, code: false);
        Assert.True(!server.Session.CodeEnglishAt("var x = "), "繰り返し切り替えても日本語に戻る");
    }

    [Test]
    public static void TipConversionKeys_SettingCanChangeWithoutChangingReconversion()
    {
        using var server = new Server();
        server.Settings.ConvertKeysSwitchIme = false;
        using (var off = server.Send(VirtualKeys.Convert, selection: false))
            Assert.True(!off.RootElement.TryGetProperty("inputMode", out _), "OFFでは入力モードを切り替えない");
        using (var selected = server.Send(VirtualKeys.Convert, selection: true, selectedText: "きょう"))
        {
            Assert.True(selected.RootElement.GetProperty("view").ValueKind == JsonValueKind.Object, "OFFでも再変換は有効");
            Assert.True(!selected.RootElement.TryGetProperty("inputMode", out _), "選択文字があるときは切り替えない");
        }
        using var cancelled = server.Send(VirtualKeys.Escape, composing: true);
        server.Settings.ConvertKeysSwitchIme = true;
        using var on = server.Send(VirtualKeys.NonConvert, selection: false);
        Assert.True(!on.RootElement.GetProperty("inputMode").GetBoolean(), "現在の設定値をキーごとに反映する");
    }

    [Test]
    public static void TipConversionKeys_SelectionAndUnknownSelectionNeverSwitch()
    {
        using var server = new Server();
        foreach (var vk in new[] { VirtualKeys.Convert, VirtualKeys.NonConvert })
        {
            foreach (var selection in new object?[] { true, null })
            {
                using var reply = server.Send(vk, selection);
                Assert.True(!reply.RootElement.TryGetProperty("inputMode", out _), "選択あり・取得失敗・旧DLLでは切り替えない");
                if (selection is true) Assert.True(reply.RootElement.GetProperty("consumed").GetBoolean(), "選択中はOS側の切り替えにも使わせない");
            }
            using var unknown = server.Send(vk, unknownSelection: true);
            Assert.True(unknown.RootElement.GetProperty("consumed").GetBoolean() && !unknown.RootElement.TryGetProperty("inputMode", out _),
                "取得失敗はキーを消費してモードを維持する");
        }
        using var tooLong = server.Send(VirtualKeys.Convert, true, new string('あ', 129));
        Assert.True(tooLong.RootElement.GetProperty("view").ValueKind == JsonValueKind.Null, "129文字の選択を再変換しない");
        Assert.True(!tooLong.RootElement.TryGetProperty("inputMode", out _), "長い選択も切り替え対象にしない");
    }

    [Test]
    public static void TipConversionKeys_ModifiersAndCompositionKeepExistingBehavior()
    {
        using var server = new Server();
        using (var modified = server.Send(VirtualKeys.Convert, selection: false, mods: 2))
            Assert.True(!modified.RootElement.TryGetProperty("inputMode", out _), "Ctrlとの組み合わせは切り替えない");
        using var typed = server.Send('K', ch: 'k');
        using var typed2 = server.Send('A', composing: true, ch: 'a');
        using var converted = server.Send(VirtualKeys.Convert, selection: false, composing: true);
        Assert.True(!converted.RootElement.TryGetProperty("inputMode", out _), "入力中は変換開始に使う");
        Assert.True(converted.RootElement.GetProperty("view").GetProperty("converting").GetBoolean(), "既存の変換処理を通る");
    }

    [Test]
    public static void TipConversionKeys_ReconversionUsesReplacementInsteadOfCommit()
    {
        using var server = new Server();
        using var started = server.Send(VirtualKeys.Convert, true, "きょう");
        Assert.Equal(0, started.RootElement.GetProperty("commits").GetArrayLength());
        using var committed = server.Send(VirtualKeys.Return, composing: true);
        Assert.True(committed.RootElement.TryGetProperty("replacement", out var replacement) && replacement.GetString()!.Length > 0,
            "確定時は元の選択範囲を置換する応答");
        Assert.Equal(0, committed.RootElement.GetProperty("commits").GetArrayLength());
    }

    [Test]
    public static void TipConversionKeys_CancelRequestKeepsOriginalAndClearsSession()
    {
        using var server = new Server();
        using var started = server.Send(VirtualKeys.Convert, true, "きょう");
        using var cancelled = server.Cancel();
        Assert.True(!server.Session.IsComposing && cancelled.RootElement.GetProperty("view").ValueKind == JsonValueKind.Null,
            "フォーカスや選択が変わった通知で終了する");
        Assert.True(!cancelled.RootElement.TryGetProperty("replacement", out _) && cancelled.RootElement.GetProperty("commits").GetArrayLength() == 0,
            "原文を変更しない");
        using var mode = server.Send(VirtualKeys.NonConvert, false);
        Assert.True(!mode.RootElement.GetProperty("inputMode").GetBoolean(), "取消後のモード切り替えもできる");
    }
}
