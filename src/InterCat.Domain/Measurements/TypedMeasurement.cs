namespace InterCat.Domain;

/// <summary>A value with all semantics required by R2 and R3.</summary>
public sealed record TypedMeasurement
{
    public TypedMeasurement(
        decimal? value,
        MeasurementUnit unit,
        Metric semanticDomain,
        AccountingSide observationSide,
        string source,
        QualityLevel quality,
        ByteDomain? byteDomain = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("A measurement source is required.", nameof(source));
        }

        Value = value;
        Unit = unit;
        SemanticDomain = semanticDomain;
        ObservationSide = observationSide;
        Source = source;
        Quality = quality;
        ByteDomain = byteDomain;
    }

    public decimal? Value { get; }
    public MeasurementUnit Unit { get; }
    public Metric SemanticDomain { get; }
    public AccountingSide ObservationSide { get; }
    public string Source { get; }
    public QualityLevel Quality { get; }
    public ByteDomain? ByteDomain { get; }
    public bool IsKnown => Value.HasValue;
}
