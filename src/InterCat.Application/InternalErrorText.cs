namespace InterCat.Application;

/// <summary>
/// The words for an error nothing in InterCat mapped (§20.6 Internal), as the command line and the window say one: a defect
/// in InterCat rather than in what was asked, named by its kind and in its own words, so whoever meets it can report it.
/// </summary>
public static class InternalErrorText
{
    /// <summary>
    /// That <paramref name="what"/> stopped on such an error, and the error itself: "What was being done stopped on an
    /// error InterCat did not expect, a defect in InterCat rather than in what was asked. InvalidCastException: …".
    /// </summary>
    public static string Stopped(string what, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        return $"{what} stopped on an error InterCat did not expect, a defect in InterCat rather than in what was asked. "
            + Named(exception);
    }

    /// <summary>
    /// The error as one sentence: its kind, then what it says, given a full stop where it ends without one, so the words
    /// that follow it start a sentence of their own.
    /// </summary>
    public static string Named(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string kind = exception.GetType().Name;
        string said = exception.Message.Trim();
        return said.Length == 0 ? kind + "."
            : said[^1] is '.' or '!' or '?' ? $"{kind}: {said}"
            : $"{kind}: {said}.";
    }
}
