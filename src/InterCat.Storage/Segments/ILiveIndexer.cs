namespace InterCat.Storage;

/// <summary>
/// What a live follow asks, as it publishes each generation, for the indexes of the generation that one extends: its
/// derivation checkpoint, which a reader of the new generation extends over the segments the new one adds rather than
/// deriving from every segment (§12.1 S1, derivation-checkpoint-v1 §2). The indexes are staged afresh into each generation
/// that names them and never carried, so a damaged one costs at most a rollback to the generation before it.
/// </summary>
public interface ILiveIndexer
{
    /// <summary>
    /// Stages into <paramref name="store"/> the indexes of its current generation, named for <paramref name="generation"/>,
    /// the generation about to be published, and completes them. None where the store has no generation or nothing to
    /// index, or where the indexes cannot be made, which costs a reader time and never fails the publication.
    /// </summary>
    IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken);
}
