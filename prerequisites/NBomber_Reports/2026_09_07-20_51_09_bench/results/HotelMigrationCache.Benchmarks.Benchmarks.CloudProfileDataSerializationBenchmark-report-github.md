```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9278)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2


```
| Method                | Mean     | Error   | StdDev  | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------------------- |---------:|--------:|--------:|------:|-------:|----------:|------------:|
| SystemTextJson        | 515.3 ns | 2.15 ns | 1.79 ns |  1.00 | 0.0658 |     312 B |        1.00 |
| GeneratedBinary       | 192.5 ns | 0.94 ns | 0.83 ns |  0.37 | 0.1121 |     528 B |        1.69 |
| GeneratedBinaryPooled | 167.7 ns | 2.07 ns | 1.73 ns |  0.33 | 0.0527 |     248 B |        0.79 |
