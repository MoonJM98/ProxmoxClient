using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App;

/// <summary>콘솔 창 등에서 특정 게스트의 전원 동작(<see cref="GuestPowerAction" />)을 실행하는 대리자(메인 뷰모델이 제공).</summary>
public delegate Task GuestPowerRunner(PveResource guest, GuestPowerAction action);
