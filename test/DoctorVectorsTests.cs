using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// doctor-vectors.json (contract B23, ADR 0002): the same ids, severities, titles and fixes as the React Native
    /// reference (0.9.3), Android, Flutter and Swift, which carry the same file. This SDK takes one certificate, so
    /// the cases that pass two are skipped.
    /// </summary>
    public class DoctorVectorsTests
    {
        [Fact]
        public void EveryVector()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "doctor-vectors.json")));
            var run = 0;
            foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var input = v.GetProperty("input");
                var certs = input.GetProperty("certSha256").EnumerateArray().Select(c => c.GetString()!).ToArray();
                if (certs.Length > 1) continue;
                var f = input.GetProperty("file");
                var file = f.ValueKind == JsonValueKind.Null ? null : new DoctorFile(
                    f.GetProperty("status").ValueKind == JsonValueKind.Null ? null : f.GetProperty("status").GetInt32(),
                    f.GetProperty("body").ValueKind == JsonValueKind.Null ? null : f.GetProperty("body").GetString());
                var appId = input.GetProperty("appId");
                var report = Doctor.Findings(input.GetProperty("publishableKey").GetString(), input.GetProperty("endpoint").GetString(),
                    input.GetProperty("platform").GetString()!, appId.ValueKind == JsonValueKind.Null ? null : appId.GetString(),
                    certs.FirstOrDefault(), file);
                var want = v.GetProperty("findings").EnumerateArray().Select(x => new DoctorFinding(
                    x.GetProperty("id").GetString()!, x.GetProperty("severity").GetString()!, x.GetProperty("title").GetString()!, x.GetProperty("fix").GetString()!)).ToList();
                var name = v.GetProperty("name").GetString();
                Assert.True(want.SequenceEqual(report.Findings), $"{name}: want [{string.Join(" | ", want)}] got [{string.Join(" | ", report.Findings)}]");
                Assert.True(v.GetProperty("ok").GetBoolean() == report.Ok, $"{name}: ok");
                run++;
            }
            Assert.True(run >= 38, $"only {run} vectors ran");
        }
    }
}
