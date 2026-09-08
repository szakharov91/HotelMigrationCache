```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9278)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2


```
| Method                | Mean     | Error   | StdDev  | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------------------- |---------:|--------:|--------:|------:|-------:|----------:|------------:|
| SystemTextJson        | 533.1 ns | 5.10 ns | 4.77 ns |  1.00 | 0.0658 |     312 B |        1.00 |
| GeneratedBinary       | 188.0 ns | 0.89 ns | 0.83 ns |  0.35 | 0.1121 |     528 B |        1.69 |
| GeneratedBinaryPooled | 164.4 ns | 0.58 ns | 0.51 ns |  0.31 | 0.0527 |     248 B |        0.79 |
