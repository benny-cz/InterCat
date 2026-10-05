namespace InterCat.Storage;

/// <summary>
/// Raised when a folder holds no published session: it is not a session's folder, or its capture has not published a
/// generation yet. It is an <see cref="InvalidOperationException"/>, as a reader's other refusals are, so whatever
/// handles those handles this; a command line can tell it apart, because naming the wrong folder is a mistake in what
/// was asked rather than something the session lacks.
/// </summary>
public sealed class NoSessionException() : InvalidOperationException(SessionStore.NoGeneration);
