namespace ProxmoxClient.Core.Models;

/// <summary>
///     One cluster task entry. Rows are kept alive across refreshes via <see cref="CopyFrom" />; only changed properties
///     are notified.
/// </summary>
public sealed class PveTask : ObservableModel
{
    public string Upid
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public string Node
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public string Type
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public string Id
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public string User
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public DateTime StartTimeUtc
    {
        get;
        set => SetField(ref field, value);
    }

    public DateTime? EndTimeUtc
    {
        get;
        set => SetField(ref field, value);
    }

    public string Status
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                Raise(nameof(IsRunning));
                Raise(nameof(IsOk));
                Raise(nameof(IsFailed));
            }
        }
    } = string.Empty;

    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);
    public bool IsOk => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => !IsRunning && !IsOk;

    /// <summary>Copies values from a fresh snapshot; each setter notifies only when its value changed.</summary>
    public void CopyFrom(PveTask other)
    {
        Upid = other.Upid;
        Node = other.Node;
        Type = other.Type;
        Id = other.Id;
        User = other.User;
        StartTimeUtc = other.StartTimeUtc;
        EndTimeUtc = other.EndTimeUtc;
        Status = other.Status;
    }
}