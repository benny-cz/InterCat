using InterCat.CaptureBroker;

Console.Error.WriteLine(
    "InterCat broker protocol v1 has an OS-authenticated pipe boundary, bounded dispatch, durable "
    + "ownership/recovery with bounded log compaction and a validated broker-owned filesystem root, "
    + "but the live capture runtime is not enabled.");
Console.Error.WriteLine(
    "The production root would be provisioned at "
    + $"{Path.Combine(BrokerRootLocation.ProductionParentDirectory, BrokerRootLocation.ProductionRootName)} "
    + "by an elevated broker; nothing is created by this invocation.");
return 3;
