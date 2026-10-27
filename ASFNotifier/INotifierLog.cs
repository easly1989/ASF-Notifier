namespace ASFNotifier;

internal interface INotifierLog {
	void Error(string message);
	void Info(string message);
	void Warning(string message);
}
