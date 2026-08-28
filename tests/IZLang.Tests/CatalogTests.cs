using System.Linq;
using IZLang.Devices;
using Xunit;

namespace IZLang.Tests
{
    public class CatalogTests
    {
        private static DeviceCatalog Sample() => new DeviceCatalog(new[]
        {
            new DeviceInfo("StructureVolumePump", -321403609, "Volume Pump", 0,
                new[]
                {
                    new LogicProperty("On", 28, LogicAccess.ReadWrite),
                    new LogicProperty("Pressure", 5, LogicAccess.Read),
                    new LogicProperty("Setting", 12, LogicAccess.Write),
                },
                new SlotProperty[0]),

            new DeviceInfo("StructureChuteInlet", 123456, "Chute Inlet", 2,
                new[] { new LogicProperty("On", 28, LogicAccess.ReadWrite) },
                new[]
                {
                    new SlotProperty("Quantity", 3),
                    new SlotProperty("OccupantHash", 2),
                }),
        }, "0.2.5678.22");

        [Fact]
        public void RoundTripPreservesEverything()
        {
            var original = Sample();
            var restored = CatalogFormat.Read(CatalogFormat.Write(original));

            Assert.Equal(original.GameVersion, restored.GameVersion);
            Assert.Equal(original.Devices.Count, restored.Devices.Count);

            for (int i = 0; i < original.Devices.Count; i++)
            {
                var a = original.Devices[i];
                var b = restored.Devices[i];

                Assert.Equal(a.PrefabName, b.PrefabName);
                Assert.Equal(a.PrefabHash, b.PrefabHash);
                Assert.Equal(a.DisplayName, b.DisplayName);
                Assert.Equal(a.SlotCount, b.SlotCount);

                Assert.Equal(a.Properties.Select(p => p.Name), b.Properties.Select(p => p.Name));
                Assert.Equal(a.Properties.Select(p => p.LogicType), b.Properties.Select(p => p.LogicType));
                Assert.Equal(a.Properties.Select(p => p.Access), b.Properties.Select(p => p.Access));

                Assert.Equal(a.SlotProperties.Select(s => s.Name), b.SlotProperties.Select(s => s.Name));
            }
        }

        [Theory]
        [InlineData(LogicAccess.ReadWrite, "rw")]
        [InlineData(LogicAccess.Read, "r")]
        [InlineData(LogicAccess.Write, "w")]
        public void AccessSurvivesTheRoundTrip(LogicAccess access, string expectedLabel)
        {
            Assert.Equal(expectedLabel, access.Label());

            var catalog = new DeviceCatalog(new[]
            {
                new DeviceInfo("X", 1, "X", 0,
                    new[] { new LogicProperty("P", 9, access) }, new SlotProperty[0]),
            }, "v");

            var restored = CatalogFormat.Read(CatalogFormat.Write(catalog));
            Assert.Equal(access, restored.Devices[0].Properties[0].Access);
        }

        [Fact]
        public void AFileTruncatedMidwayStillLoadsWhatItCan()
        {
            // A crash during the write must not break completion entirely: the devices
            // already written still count.
            var devices = Enumerable.Range(0, 50).Select(i =>
                new DeviceInfo("Structure" + i, i, "Name " + i, 0,
                    new[] { new LogicProperty("On", 28, LogicAccess.ReadWrite) },
                    new SlotProperty[0])).ToArray();

            string text = CatalogFormat.Write(new DeviceCatalog(devices, "v"));
            var catalog = CatalogFormat.Read(text.Substring(0, text.Length / 2));

            Assert.NotEmpty(catalog.Devices);
            Assert.True(catalog.Devices.Count < devices.Length);
            Assert.Equal("Structure0", catalog.Devices[0].PrefabName);
        }

        [Fact]
        public void ADifferentFormatVersionReturnsEmpty()
        {
            // That way the caller knows it has to regenerate, instead of reading garbage.
            var catalog = CatalogFormat.Read("V\t999\nD\tX\t1\t0\tX\n");
            Assert.True(catalog.IsEmpty);
        }

        [Fact]
        public void AMalformedLineIsSkipped()
        {
            var catalog = CatalogFormat.Read(
                "V\t1\n" +
                "D\tGood\t42\t0\tGood\n" +
                "P\tOn\tnot-a-number\trw\n" +      // skipped
                "P\tSetting\t12\trw\n" +
                "random junk with no tab\n" +
                "D\tOther\tnot-a-number\t0\tX\n" + // device skipped
                "D\tSecond\t43\t0\tSecond\n");

            Assert.Equal(2, catalog.Devices.Count);
            Assert.Equal("Good", catalog.Devices[0].PrefabName);
            Assert.Equal(new[] { "Setting" }, catalog.Devices[0].Properties.Select(p => p.Name));
            Assert.Equal("Second", catalog.Devices[1].PrefabName);
        }

        [Fact]
        public void EmptyTextReturnsAnEmptyCatalog()
        {
            Assert.True(CatalogFormat.Read("").IsEmpty);
            Assert.True(CatalogFormat.Read("# just a comment\n").IsEmpty);
        }

        [Fact]
        public void ADisplayNameWithATabDoesNotBreakTheFormat()
        {
            var catalog = new DeviceCatalog(new[]
            {
                new DeviceInfo("X", 1, "Name\twith\ttab\nand break", 0,
                    new[] { new LogicProperty("On", 28, LogicAccess.Read) }, new SlotProperty[0]),
            }, "v");

            var restored = CatalogFormat.Read(CatalogFormat.Write(catalog));

            Assert.Single(restored.Devices);
            Assert.Single(restored.Devices[0].Properties);
            Assert.DoesNotContain('\t', restored.Devices[0].DisplayName);
        }

        [Fact]
        public void SearchByName()
        {
            var catalog = Sample();

            Assert.Equal("StructureVolumePump", catalog.FindByName("StructureVolumePump")!.PrefabName);
            Assert.Null(catalog.FindByName("DoesNotExist"));
            Assert.Equal("StructureVolumePump", catalog.FindByHash(-321403609)!.PrefabName);
        }

        [Fact]
        public void SearchSortsByMatchPositionAndNotAlphabetically()
        {
            // Every prefab starts with "Structure", so alphabetical order would put
            // CircuitHousingSolar ahead of SolarPanel for someone who typed "Solar".
            var catalog = new DeviceCatalog(new[]
            {
                Device("StructureCircuitHousingSolar"),
                Device("StructureSolarPanelDual"),
                Device("StructureSolarPanel"),
            }, "v");

            var results = catalog.Search("Solar").Select(d => d.PrefabName).ToList();

            Assert.Equal(
                new[] { "StructureSolarPanel", "StructureSolarPanelDual", "StructureCircuitHousingSolar" },
                results);
        }

        [Fact]
        public void SearchIsCaseInsensitive()
        {
            Assert.Contains(Sample().Search("volumepump"), d => d.PrefabName == "StructureVolumePump");
        }

        [Fact]
        public void SearchRespectsTheLimit()
        {
            var devices = Enumerable.Range(0, 200).Select(i => Device("Structure" + i)).ToArray();
            var catalog = new DeviceCatalog(devices, "v");

            Assert.Equal(10, catalog.Search("Structure", limit: 10).Count);
        }

        [Fact]
        public void FindPropertyFindsByName()
        {
            var pump = Sample().FindByName("StructureVolumePump")!;

            Assert.Equal(LogicAccess.Read, pump.FindProperty("Pressure")!.Access);
            Assert.Null(pump.FindProperty("DoesNotExist"));
        }

        [Fact]
        public void JsonComesOutWellFormedForExternalUse()
        {
            string json = CatalogFormat.WriteJson(Sample());

            Assert.StartsWith("{", json.TrimStart());
            Assert.EndsWith("}", json.TrimEnd());
            Assert.Contains("\"prefabName\": \"StructureVolumePump\"", json);
            Assert.Contains("\"logicType\": 5", json);
            Assert.Contains("\"read\": true", json);
            Assert.Contains("\"write\": false", json);
            Assert.Contains("\"slotProperties\"", json);

            // Balanced braces and brackets: a cheap proof it did not come out truncated.
            Assert.Equal(json.Count(c => c == '{'), json.Count(c => c == '}'));
            Assert.Equal(json.Count(c => c == '['), json.Count(c => c == ']'));
        }

        [Fact]
        public void JsonEscapesQuotesAndOddNames()
        {
            var catalog = new DeviceCatalog(new[]
            {
                new DeviceInfo("X", 1, "quote \" slash \\ end", 0,
                    new LogicProperty[0], new SlotProperty[0]),
            }, "v");

            string json = CatalogFormat.WriteJson(catalog);
            Assert.Contains("\\\"", json);
            Assert.Contains("\\\\", json);
        }

        private static DeviceInfo Device(string name) =>
            new DeviceInfo(name, name.GetHashCode(), name, 0, new LogicProperty[0], new SlotProperty[0]);
    }
}
