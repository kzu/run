#:include System/Base62.cs
#:include System/QuaranTime.cs

using System;

Console.WriteLine(Guid.CreateVersion7().ToString("N"));
Console.WriteLine(Base62.Encode(DateTimeOffset.UtcNow.UtcTicks - QuaranTime.QuarantineEpochTicks));

var time = QuaranTime.ToQuaranTimeSeconds(DateTimeOffset.UtcNow + TimeSpan.FromDays(100000));
var value = $"aa6d35d6b3e8efb2599aadbb{Base62.Encode(time)}";
Console.WriteLine($"{value} ({value.Length})");