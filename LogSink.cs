namespace YellowInsideLib;

record LogSink(
    Action<string>? InfoLog = null,
    Action<string>? WarnLog = null,
    Action<string>? ErrorLog = null)
{
    public void Info(string message) => InfoLog?.Invoke(message);
    public void Warn(string message) => WarnLog?.Invoke(message);
    public void Error(string message) => ErrorLog?.Invoke(message);
}
