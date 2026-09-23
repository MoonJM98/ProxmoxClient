using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

public partial class OptionsTab
{
    /// <summary>
    ///     목록 한 줄 — 지금 값(<see cref="Display" />)과 재시작 후 적용될 값(<see cref="PendingText" />).
    ///     웹 UI 처럼 대기 중 값은 지금 값과 다르게 보일 때만 따로 보여 준다.
    /// </summary>
    public sealed class OptionRow
    {
        private readonly GuestPendingConfig _config;

        public OptionRow(GuestOption option, GuestPendingConfig config)
        {
            Option = option;
            _config = config;
            Raw = config.Current.GetValueOrDefault(option.Key, "");
            EffectiveRaw = config.Effective.GetValueOrDefault(option.Key, "");
            HasPending = config.HasPending(option.KeysForPending);
        }

        /// <summary>값 하나만으로 만든다(대기 중 변경 없음).</summary>
        public OptionRow(GuestOption option, string raw)
            : this(option, GuestPendingConfig.FromConfig(raw.Length > 0
                ? new Dictionary<string, string> { [option.Key] = raw }
                : new Dictionary<string, string>()))
        {
        }

        public GuestOption Option { get; }

        /// <summary>지금 적용된 값.</summary>
        public string Raw { get; }

        /// <summary>대기 중 변경까지 반영한 값 — 편집은 이 값에서 시작한다.</summary>
        public string EffectiveRaw { get; }

        public bool HasPending { get; }

        public string Label => Option.Label;

        public string Display => Render(Raw, _config.Current);

        /// <summary>대기 중 값 — 삭제 예정이면 "삭제 예정(기본값)" 을, 아니면 반영 후 모습을 보인다.</summary>
        public string PendingText
        {
            get
            {
                if (!HasPending) return string.Empty;

                var after = Render(EffectiveRaw, _config.Effective);
                // 이 줄의 값 키가 지워지거나, (지금 있는) 키가 모두 지워질 예정이면 삭제로 보인다(웹 UI 의 취소선)
                var present = Option.KeysForPending.Where(_config.Current.ContainsKey).ToList();
                var deleted = _config.DeletedKeys.Contains(Option.Key)
                              || present.Count > 0 && present.All(_config.DeletedKeys.Contains);
                if (deleted) return Loc.T("OptionsTab_PendingDelete", after);
                return after == Display ? string.Empty : after;
            }
        }

        private string Render(string raw, IReadOnlyDictionary<string, string> config)
        {
            if (Option.Editor?.Display is { } display) return raw.Length > 0 ? display(raw) : EmptyText();

            return Option.Kind switch
            {
                // 설정에 없으면 서버 기본값 설명을 보여 준다(기본 켜짐인 항목을 '아니요'로 잘못 보이지 않게)
                OptionKind.Bool when raw.Length == 0 && Option.EmptyLabelKey is not null => EmptyText(),
                OptionKind.Bool => Loc.T(raw == "1" ? "GuestOptions_Yes" : "GuestOptions_No"),
                OptionKind.Choice => ChoiceLabel(raw),
                OptionKind.OsType => OsTypes.Describe(raw),
                OptionKind.BootOrder => BootOrder.Describe(raw, config) is { Length: > 0 } order ? order : EmptyText(),
                _ => raw.Length > 0 ? raw : EmptyText()
            };
        }

        private string ChoiceLabel(string raw)
        {
            var match = Option.Choices?.FirstOrDefault(c =>
                string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase));
            if (match is { Label: { Length: > 0 } label }) return Loc.T(label);

            return raw.Length > 0 ? raw : EmptyText();
        }

        private string EmptyText()
        {
            return Loc.T(Option.EmptyLabelKey ?? "GuestOptions_NotSet");
        }
    }
}
