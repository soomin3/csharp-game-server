using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Network;
using PacketGenerator;

namespace Game.Tests
{
    public class PacketGeneratorTests
    {
        private const string Schema = """
            {
              "packets": [
                { "name": "EchoReq", "id": 1, "idName": "Echo", "direction": "clientToServer", "fields": [{ "name": "Message", "type": "string" }] },
                { "name": "EchoRes", "id": 1, "idName": "Echo", "direction": "serverToClient", "fields": [{ "name": "Message", "type": "string" }] }
              ]
            }
            """;

        [Fact]
        public void 기존_패킷_번호를_유지한다()
        {
            Assert.Equal(1, (int)PacketId.Echo);
            Assert.Equal(2, (int)PacketId.Login);
            Assert.Equal(3, (int)PacketId.Chat);
            Assert.Equal(4, (int)PacketId.MoveReq);
            Assert.Equal(5, (int)PacketId.MoveNtf);
            Assert.Equal(6, (int)PacketId.PlayerEnterNtf);
            Assert.Equal(7, (int)PacketId.PlayerLeaveNtf);
        }

        [Fact]
        public void 정의_순서가_달라도_생성_결과는_같다()
        {
            IReadOnlyDictionary<string, string> first = PacketGeneratorEngine.Render(Schema);
            JsonObject reordered = JsonNode.Parse(Schema)!.AsObject();
            JsonArray packets = reordered["packets"]!.AsArray();
            JsonNode firstPacket = packets[0]!.DeepClone();
            JsonNode secondPacket = packets[1]!.DeepClone();
            packets[0] = secondPacket;
            packets[1] = firstPacket;

            IReadOnlyDictionary<string, string> second = PacketGeneratorEngine.Render(reordered.ToJsonString());

            Assert.Equal(5, first.Count);
            Assert.Equal(first.Keys.OrderBy(path => path), second.Keys.OrderBy(path => path));
            Assert.All(first, pair => Assert.Equal(pair.Value, second[pair.Key]));
        }

        [Theory]
        [InlineData("duplicate id")]
        [InlineData("duplicate name")]
        [InlineData("invalid name")]
        [InlineData("invalid field name")]
        [InlineData("duplicate field")]
        [InlineData("unsupported type")]
        [InlineData("invalid direction")]
        [InlineData("zero id")]
        [InlineData("oversized id")]
        [InlineData("inconsistent id name")]
        [InlineData("reserved id name")]
        [InlineData("invalid handler name")]
        [InlineData("empty fields")]
        [InlineData("null packet")]
        [InlineData("null field")]
        [InlineData("request suffix")]
        [InlineData("response suffix")]
        [InlineData("framework type name")]
        [InlineData("reserved handler name")]
        [InlineData("intrinsic field name")]
        public void 잘못된_정의는_생성을_거절한다(string scenario)
        {
            JsonObject document = JsonNode.Parse(Schema)!.AsObject();
            JsonArray packets = document["packets"]!.AsArray();
            JsonObject request = packets[0]!.AsObject();
            JsonObject response = packets[1]!.AsObject();
            JsonArray fields = request["fields"]!.AsArray();
            switch (scenario)
            {
                case "duplicate id": response["direction"] = "clientToServer"; break;
                case "duplicate name": response["name"] = "echoReq"; break;
                case "invalid name": request["name"] = "../Outside"; break;
                case "invalid field name": fields[0]!["name"] = "ToPacket"; break;
                case "duplicate field": fields.Add(fields[0]!.DeepClone()); break;
                case "unsupported type": fields[0]!["type"] = "double"; break;
                case "invalid direction": request["direction"] = "both"; break;
                case "zero id": request["id"] = 0; break;
                case "oversized id": request["id"] = 65536; break;
                case "inconsistent id name": response["idName"] = "Other"; break;
                case "reserved id name": request["idName"] = "None"; break;
                case "invalid handler name": request["handler"] = "Bad-Handler"; break;
                case "empty fields": fields.Clear(); break;
                case "null packet": packets[0] = null; break;
                case "null field": fields[0] = null; break;
                case "request suffix": request["name"] = "BadRes"; break;
                case "response suffix": response["name"] = "BadReq"; break;
                case "framework type name": request["name"] = "Array"; break;
                case "reserved handler name": request["handler"] = "IPacketHandler"; break;
                case "intrinsic field name": fields[0]!["name"] = "__arglist"; break;
                default: throw new InvalidOperationException(scenario);
            }

            Assert.Throws<ArgumentException>(() => PacketGeneratorEngine.Render(document.ToJsonString()));
        }

        [Fact]
        public void 알_수_없는_JSON_속성은_오타로_거절한다()
        {
            JsonObject document = JsonNode.Parse(Schema)!.AsObject();
            document["pakcets"] = new JsonArray();
            Assert.Throws<JsonException>(() => PacketGeneratorEngine.Render(document.ToJsonString()));
        }

        [Fact]
        public void 잘못된_정의는_출력_폴더를_만들지_않는다()
        {
            using TemporaryDirectory directory = new TemporaryDirectory();
            string definitionPath = Path.Combine(directory.Path, "invalid.json");
            File.WriteAllText(definitionPath, "{\"packets\":[]}");
            string outputPath = Path.Combine(directory.Path, "output");

            Assert.Throws<ArgumentException>(() => PacketGeneratorEngine.Generate(definitionPath, outputPath));

            Assert.False(Directory.Exists(outputPath));
        }

        [Fact]
        public void 같은_정의를_다시_생성하면_기존_파일을_다시_쓰지_않는다()
        {
            using TemporaryDirectory directory = new TemporaryDirectory();
            string definitionPath = Path.Combine(directory.Path, "packets.json");
            File.WriteAllText(definitionPath, Schema);
            IReadOnlyList<string> paths = PacketGeneratorEngine.Generate(definitionPath, directory.Path);
            DateTime timestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            foreach (string path in paths)
            {
                File.SetLastWriteTimeUtc(path, timestamp);
            }

            PacketGeneratorEngine.Generate(definitionPath, directory.Path);

            Assert.All(paths, path => Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path)));
        }

        [Fact]
        public void 없어진_정의는_생성기_표시가_있는_파일만_정리한다()
        {
            using TemporaryDirectory directory = new TemporaryDirectory();
            string definitionPath = Path.Combine(directory.Path, "packets.json");
            File.WriteAllText(definitionPath, Schema);
            PacketGeneratorEngine.Generate(definitionPath, directory.Path);
            string output = Path.Combine(directory.Path, "Common", "Network", "Generated");
            string stale = Path.Combine(output, "Removed.g.cs");
            string manual = Path.Combine(output, "Manual.g.cs");
            File.WriteAllText(stale, "// <auto-generated by MyServer PacketGenerator>\nold");
            File.WriteAllText(manual, "// Handwritten file");

            PacketGeneratorEngine.Generate(definitionPath, directory.Path);

            Assert.False(File.Exists(stale));
            Assert.Equal("// Handwritten file", File.ReadAllText(manual));
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; }

            public TemporaryDirectory()
            {
                Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyServer-packet-generator-tests-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                string absolute = System.IO.Path.GetFullPath(Path);
                string prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
                if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !System.IO.Path.GetFileName(absolute).StartsWith("MyServer-packet-generator-tests-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean an unexpected temporary directory.");
                }
                Directory.Delete(absolute, recursive: true);
            }
        }
    }
}
