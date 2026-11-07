using System.Text.Json.Serialization;

namespace ProxmoxClient.UI;

/// <summary>
///     UI 가 저장하는 작은 JSON 파일의 직렬화 코드(빌드 때 생성 — 트리밍에 안전).
///     터미널 명령 기록(terminal-history.json), 열린 터미널 목록(open-terminals.txt).
/// </summary>
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<Platform.OpenTerminalEntry>))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
