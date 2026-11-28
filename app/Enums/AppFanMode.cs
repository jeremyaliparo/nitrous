namespace Nitrous.Enums;

/// <summary>
/// The fan behaviour an app rule asks for. <see cref="Leave"/> means the rule does not touch the
/// fans; the other values map onto Nitrous' existing fan modes.
/// </summary>
public enum AppFanMode
{
    Leave,
    Auto,
    Max,
    Custom
}
