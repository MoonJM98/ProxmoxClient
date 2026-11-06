using System.Text.Json.Serialization;

namespace ProxmoxClient.UI;

/// <summary>UI 가 저장하는 작은 JSON 파일의 직렬화 코드(빌드 때 생성 — 트리밍에 안전). 터미널 명령 기록(terminal-history.json).</summary>
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
