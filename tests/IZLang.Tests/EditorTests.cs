using System.Collections.Generic;
using System.Linq;
using IZLang.Binding;
using IZLang.Devices;
using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Completion and hover. The <c>|</c> mark in the source says where the caret is
    /// and is removed before compiling - it keeps the test readable without counting offsets.
    /// </summary>
    public class EditorTests
    {
        // ------------------------------------------------------------------
        //  Fake catalog, with devices that behave differently
        // ------------------------------------------------------------------

        private const int LogicOn = 28;
        private const int LogicPressure = 5;
        private const int LogicSetting = 12;
        private const int LogicTemperature = 6;
        private const int SlotQuantity = 3;

        private static DeviceInfo Pump() => new DeviceInfo(
            "StructureVolumePump", PrefabHash.Compute("StructureVolumePump"), "Volume Pump", 0,
            new[]
            {
                new LogicProperty("On", LogicOn, LogicAccess.ReadWrite),
                new LogicProperty("Setting", LogicSetting, LogicAccess.ReadWrite),
                new LogicProperty("Pressure", LogicPressure, LogicAccess.Read),
            },
            new SlotProperty[0]);

        private static DeviceInfo Sensor() => new DeviceInfo(
            "StructureGasSensor", PrefabHash.Compute("StructureGasSensor"), "Gas Sensor", 0,
            new[]
            {
                new LogicProperty("Pressure", LogicPressure, LogicAccess.Read),
                new LogicProperty("Temperature", LogicTemperature, LogicAccess.Read),
            },
            new SlotProperty[0]);

        private static DeviceInfo Chute() => new DeviceInfo(
            "StructureChuteInlet", PrefabHash.Compute("StructureChuteInlet"), "Chute Inlet", 2,
            new[] { new LogicProperty("On", LogicOn, LogicAccess.ReadWrite) },
            new[] { new SlotProperty("Quantity", SlotQuantity) });

        private static MemoryEditorEnvironment Environment()
        {
            var catalog = new DeviceCatalog(new[] { Pump(), Sensor(), Chute() }, "test");
            return new MemoryEditorEnvironment { Catalog = catalog };
        }

        /// <summary>Splits the source from the caret offset marked with '|'.</summary>
        private static (string Source, int Caret) Split(string marked)
        {
            int caret = marked.IndexOf('|');
            Assert.True(caret >= 0, "the test source has to mark the caret with '|'");
            return (marked.Remove(caret, 1), caret);
        }

        private static CompletionEngine.CompletionResult Complete(string marked,
                                                                  IEditorEnvironment? environment = null)
        {
            var (source, caret) = Split(marked);
            return CompletionEngine.GetCompletions(source, caret, environment);
        }

        private static List<string> Labels(CompletionEngine.CompletionResult result) =>
            result.Items.Select(i => i.Label).ToList();

        // ==================================================================
        //  Completion - context
        // ==================================================================

        [Fact]
        public void AfterADotOnAListItSuggestsTheQueryMethods()
        {
            var result = Complete("fn main() { var xs: list num[8]; xs.| }");

            Assert.Equal(CompletionContext.ListMethod, result.Context);

            var labels = Labels(result);
            Assert.Contains("where", labels);
            Assert.Contains("sum", labels);
            Assert.Contains("orderBy", labels);
            Assert.Contains("add", labels);          // only a list can grow
            Assert.Contains("count", labels);
            Assert.DoesNotContain("Setting", labels);
        }

        [Fact]
        public void AnArrayIsOfferedTheMethodsThatDoNotChangeIt()
        {
            var result = Complete("fn main() { var a: num[4]; a.| }");

            Assert.Equal(CompletionContext.ListMethod, result.Context);

            var labels = Labels(result);
            Assert.Contains("avg", labels);
            Assert.DoesNotContain("add", labels);
            Assert.DoesNotContain("clear", labels);
        }

        [Fact]
        public void AfterAQueryMethodItSuggestsTheNextOne()
        {
            var result = Complete(
                "fn main() { var xs: list num[8]; var n = xs.where(x => x > 1).| }");

            Assert.Equal(CompletionContext.ListMethod, result.Context);

            var labels = Labels(result);
            Assert.Contains("sum", labels);
            Assert.DoesNotContain("add", labels);    // the query is not the list
        }

        [Fact]
        public void AnItemOfAListOfStructsStillSuggestsItsFields()
        {
            var result = Complete(
                "struct Job { id: num; done: bool; } " +
                "fn main() { var jobs: list Job[4]; jobs[0].| }");

            Assert.Equal(CompletionContext.StructField, result.Context);

            var labels = Labels(result);
            Assert.Contains("id", labels);
            Assert.Contains("done", labels);
        }

        [Fact]
        public void AfterDeviceEqualsItSuggestsPins()
        {
            var result = Complete("device pump = |");

            // The pins come first; the two selectors follow, because a device does not
            // have to be on a cable.
            Assert.Equal(CompletionContext.Pin, result.Context);
            Assert.Equal(new[] { "d0", "d1", "d2", "d3", "d4", "d5", "db", "all", "named" },
                         Labels(result));
        }

        [Fact]
        public void APinShowsWhatIsWiredToIt()
        {
            var environment = Environment();
            environment.Wire(0, Pump(), "north pump");
            environment.Wire(1, Sensor());

            var result = Complete("device x = |", environment);

            var d0 = result.Items.Single(i => i.Label == "d0");
            var d1 = result.Items.Single(i => i.Label == "d1");
            var d2 = result.Items.Single(i => i.Label == "d2");

            Assert.Contains("north pump", d0.Detail);
            Assert.Contains("Volume Pump", d0.Detail);
            Assert.Contains("Gas Sensor", d1.Detail);
            Assert.Contains("empty", d2.Detail);
        }

        [Fact]
        public void AfterADotOnADeviceItSuggestsOnlyThatEquipmentsProperties()
        {
            var environment = Environment();
            environment.Wire(0, Pump());

            var result = Complete(
                "device pump = d0;\n" +
                "fn main() { pump.| }\n", environment);

            Assert.Equal(CompletionContext.DeviceProperty, result.Context);

            var labels = Labels(result);
            Assert.Contains("On", labels);
            Assert.Contains("Setting", labels);
            Assert.Contains("Pressure", labels);

            // The whole point of the feature: not dumping the game's 358 properties.
            Assert.DoesNotContain("Temperature", labels);
            Assert.DoesNotContain("SolarAngle", labels);
        }

        [Fact]
        public void APropertyShowsAccessAndCurrentValue()
        {
            var environment = Environment();
            environment.Wire(0, Pump());
            environment.SetValue(0, LogicPressure, 101.325);

            var result = Complete(
                "device pump = d0;\n" +
                "fn main() { pump.| }\n", environment);

            var pressure = result.Items.Single(i => i.Label == "Pressure");
            Assert.Contains("r", pressure.Detail);
            Assert.Contains("101.325", pressure.Detail);

            var on = result.Items.Single(i => i.Label == "On");
            Assert.Contains("rw", on.Detail);
        }

        [Fact]
        public void AnEmptyPinFallsBackToTheGamesFullList()
        {
            // Without knowing the equipment, offering everything beats offering nothing.
            // The full list has hundreds of names and is trimmed to fit the popup, so
            // what matters is that typing finds what you are looking for.
            var environment = Environment();

            Assert.Contains("Pressure", Labels(Complete(
                "device pump = d3;\n" +
                "fn main() { pump.Pres| }\n", environment)));

            Assert.Contains("SolarAngle", Labels(Complete(
                "device pump = d3;\n" +
                "fn main() { pump.Solar| }\n", environment)));
        }

        [Fact]
        public void TheFallbackListIsTrimmedToFitThePopup()
        {
            // 358 LogicTypes do not fit in a list; without trimming the UI is useless.
            var result = Complete(
                "device pump = d3;\n" +
                "fn main() { pump.| }\n", Environment());

            Assert.InRange(result.Items.Count, 1, 80);
        }

        [Fact]
        public void ThePrefixFiltersAndWhateverStartsWithItComesFirst()
        {
            var environment = Environment();
            environment.Wire(0, Pump());

            var result = Complete(
                "device pump = d0;\n" +
                "fn main() { pump.Se| }\n", environment);

            Assert.Equal("Se", result.Prefix);
            Assert.Equal("Setting", result.Items[0].Label);
        }

        [Fact]
        public void InsideAllItSuggestsPrefabs()
        {
            var result = Complete("fn main() { all(|).On = true; }", Environment());

            Assert.Equal(CompletionContext.Prefab, result.Context);
            var labels = Labels(result);
            Assert.Contains("StructureVolumePump", labels);
            Assert.Contains("StructureGasSensor", labels);
        }

        [Fact]
        public void InsideAllWithAPrefixItFiltersThePrefabs()
        {
            var result = Complete("fn main() { all(StructureGas|).On = true; }", Environment());

            Assert.Equal(CompletionContext.Prefab, result.Context);
            Assert.Equal(new[] { "StructureGasSensor" }, Labels(result));
        }

        [Fact]
        public void InsideAHashLiteralItSuggestsPrefabs()
        {
            var result = Complete("fn main() { all(#\"Structure|\").On = true; }", Environment());

            Assert.Equal(CompletionContext.PrefabString, result.Context);
            Assert.Contains("StructureVolumePump", Labels(result));
        }

        [Fact]
        public void AfterSlotItSuggestsSlotProperties()
        {
            var environment = Environment();
            environment.Wire(2, Chute());

            var result = Complete(
                "device chute = d2;\n" +
                "fn main() { var q = chute.slot[0].| }\n", environment);

            Assert.Equal(CompletionContext.SlotProperty, result.Context);
            Assert.Equal(new[] { "Quantity" }, Labels(result));
        }

        [Fact]
        public void ADeviceWithSlotsOffersTheSlotMember()
        {
            var environment = Environment();
            environment.Wire(2, Chute());

            var result = Complete(
                "device chute = d2;\n" +
                "fn main() { chute.| }\n", environment);

            Assert.Contains("slot", Labels(result));
        }

        [Fact]
        public void TheGeneralContextOffersDeclaredNamesKeywordsAndBuiltins()
        {
            var result = Complete(
                "device pump = d0;\n" +
                "const MAX = 10;\n" +
                "fn adjust(x: num) { }\n" +
                "fn main() { | }\n", Environment());

            Assert.Equal(CompletionContext.General, result.Context);
            var labels = Labels(result);

            Assert.Contains("pump", labels);
            Assert.Contains("MAX", labels);
            Assert.Contains("adjust", labels);
            Assert.Contains("x", labels);          // parameter
            Assert.Contains("while", labels);      // keyword
            Assert.Contains("sqrt", labels);       // builtin
            Assert.Contains("sleep", labels);
        }

        [Fact]
        public void DeclaredNamesComeBeforeKeywords()
        {
            var result = Complete(
                "device lamp = d0;\n" +
                "fn main() { l| }\n", Environment());

            // 'lamp' is declared; 'loop' is a keyword. The declared one wins.
            var labels = Labels(result);
            Assert.True(labels.IndexOf("lamp") < labels.IndexOf("loop"),
                "expected the declared name before the keyword: " + string.Join(", ", labels));
        }

        [Fact]
        public void AFunctionDeclaredAfterTheCaretIsAlsoOffered()
        {
            // Lexical scanning, not parsing: it finds declarations that come later.
            var result = Complete(
                "fn main() { | }\n" +
                "fn helper() { }\n", Environment());

            Assert.Contains("helper", Labels(result));
        }

        [Fact]
        public void CompletionWorksOnIncompleteSource()
        {
            // The normal case while typing: nothing is closed.
            string[] fragments =
            {
                "device pump = d0;\nfn main() { pump.|",
                "fn main() { all(|",
                "device |",
                "fn main() { if |",
                "|",
                "fn main() { x.slot[0].|",
            };

            foreach (var fragment in fragments)
            {
                var result = Complete(fragment, Environment());
                Assert.NotNull(result);          // it did not throw: that is what matters
            }
        }

        // ==================================================================
        //  Completion - when the list opens by itself
        // ==================================================================

        private static CompletionEngine.CompletionResult CompleteAuto(string marked,
                                                                     IEditorEnvironment? environment = null)
        {
            var (source, caret) = Split(marked);
            return CompletionEngine.GetCompletions(source, caret, environment,
                                                   CompletionTrigger.Automatic);
        }

        [Fact]
        public void ABlankLineDoesNotOpenTheListByItself()
        {
            // With no prefix and no context, the full list would only get in the way:
            // it would cover the lines below without anyone asking for it.
            var result = CompleteAuto(
                "device pump = d0;\n" +
                "fn main() {\n" +
                "    |\n" +
                "}\n", Environment());

            Assert.Equal(CompletionContext.General, result.Context);
            Assert.Empty(result.Items);
        }

        [Fact]
        public void OneTypedCharacterAlreadyOpensTheList()
        {
            var result = CompleteAuto(
                "device pump = d0;\n" +
                "fn main() { p| }\n", Environment());

            Assert.Contains("pump", Labels(result));
        }

        [Fact]
        public void AContextThatIsAlreadyARequestOpensWithoutAPrefix()
        {
            var environment = Environment();
            environment.Wire(0, Pump());

            // '.', 'device x = ' and 'all(' are the request themselves: there the full
            // list is exactly what is wanted, even with nothing typed.
            Assert.NotEmpty(CompleteAuto("device pump = d0;\nfn main() { pump.| }\n", environment).Items);
            Assert.NotEmpty(CompleteAuto("device x = |", environment).Items);
            Assert.NotEmpty(CompleteAuto("fn main() { all(|).On = true; }", environment).Items);
        }

        [Fact]
        public void CtrlSpaceShowsEverythingOnABlankLine()
        {
            // The same caret as the blank line test, now with an explicit request.
            var result = Complete(
                "device pump = d0;\n" +
                "fn main() {\n" +
                "    |\n" +
                "}\n", Environment());

            Assert.Equal(CompletionContext.General, result.Context);
            Assert.Contains("pump", Labels(result));
            Assert.Contains("while", Labels(result));
        }

        [Fact]
        public void TheRangeToReplaceCoversTheTypedPrefix()
        {
            var environment = Environment();
            environment.Wire(0, Pump());

            var (source, caret) = Split("device pump = d0;\nfn main() { pump.Set| }\n");
            var result = CompletionEngine.GetCompletions(source, caret, environment);

            var item = result.Items.Single(i => i.Label == "Setting");
            Assert.Equal("Set", source.Substring(item.ReplaceSpan.Start, item.ReplaceSpan.Length));
            Assert.Equal(caret, item.ReplaceSpan.End);
        }

        // ==================================================================
        //  Hover
        // ==================================================================

        private static HoverInfo Hover(string marked, IEditorEnvironment? environment = null)
        {
            var (source, caret) = Split(marked);
            return HoverEngine.GetHover(source, caret, environment);
        }

        [Fact]
        public void HoverOnADeviceShowsPinEquipmentAndValues()
        {
            var environment = Environment();
            environment.Wire(0, Pump(), "north pump");
            environment.SetValue(0, LogicPressure, 101.325);
            environment.SetValue(0, LogicOn, 1);

            var hover = Hover(
                "device pump = d0;\n" +
                "fn main() { pu|mp.On = true; }\n", environment);

            Assert.Equal(HoverKind.Device, hover.Kind);
            Assert.Equal("pump = d0", hover.Title);

            string text = hover.ToText();
            Assert.Contains("Volume Pump", text);
            Assert.Contains("north pump", text);
            Assert.Contains("StructureVolumePump", text);
            Assert.Contains("Pressure", text);
            Assert.Contains("101.325", text);
            Assert.Contains("On", text);
        }

        [Fact]
        public void HoverOnADeviceWithAnEmptyPinSaysItIsEmpty()
        {
            var hover = Hover(
                "device pump = d4;\n" +
                "fn main() { pu|mp.On = true; }\n", Environment());

            Assert.Equal(HoverKind.Device, hover.Kind);
            Assert.Contains("empty", hover.ToText());
        }

        [Fact]
        public void HoverOnAPropertyShowsAccessAndValue()
        {
            var environment = Environment();
            environment.Wire(0, Pump());
            environment.SetValue(0, LogicPressure, 42.0);

            var hover = Hover(
                "device pump = d0;\n" +
                "fn main() { var p = pump.Pres|sure; }\n", environment);

            Assert.Equal(HoverKind.DeviceProperty, hover.Kind);
            Assert.Equal("Pressure", hover.Title);

            string text = hover.ToText();
            Assert.Contains("read only", text);
            Assert.Contains("42", text);
        }

        [Fact]
        public void HoverWarnsWhenTheDeviceDoesNotAcceptTheProperty()
        {
            var environment = Environment();
            environment.Wire(0, Sensor());        // the sensor has no 'Setting'

            var hover = Hover(
                "device s = d0;\n" +
                "fn main() { s.Sett|ing = 1; }\n", environment);

            Assert.Contains("does NOT accept", hover.ToText());
        }

        [Fact]
        public void HoverOnAHashLiteralShowsTheValueAndTheEquipment()
        {
            var hover = Hover(
                "fn main() { all(#\"Structure|VolumePump\").On = true; }", Environment());

            Assert.Equal(HoverKind.Prefab, hover.Kind);
            string text = hover.ToText();
            Assert.Contains("Volume Pump", text);
            Assert.Contains(PrefabHash.Compute("StructureVolumePump").ToString(), text);
        }

        [Fact]
        public void HoverReportsANonExistentPrefab()
        {
            var hover = Hover(
                "fn main() { all(#\"Structure|DoesNotExist\").On = true; }", Environment());

            Assert.Contains("no prefab exists", hover.ToText());
        }

        [Fact]
        public void HoverOnAKeywordExplainsWhatItDoes()
        {
            var hover = Hover("fn main() { yi|eld; }");

            Assert.Equal(HoverKind.Keyword, hover.Kind);
            Assert.Contains("gives the tick back", hover.ToText());
        }

        [Fact]
        public void HoverOnABuiltinShowsTheArity()
        {
            var hover = Hover("fn main() { var x = cla|mp(1, 0, 2); }");

            Assert.Equal(HoverKind.Builtin, hover.Kind);
            Assert.Contains("3", hover.Title);
        }

        [Fact]
        public void HoverOnAConstantAndOnAFunction()
        {
            Assert.Equal(HoverKind.Constant,
                Hover("const MA|X = 10;\nfn main() { }").Kind);

            Assert.Equal(HoverKind.Function,
                Hover("fn hel|per() { }\nfn main() { }").Kind);
        }

        [Fact]
        public void HoverOnWhitespaceReturnsNothing()
        {
            Assert.True(Hover("fn main() {| }").IsEmpty);
        }

        [Fact]
        public void HoverDoesNotThrowOnIncompleteSource()
        {
            string[] fragments = { "device |", "fn main() { pump.|", "all(|", "#\"|", "|" };

            foreach (var fragment in fragments)
            {
                var hover = Hover(fragment, Environment());
                Assert.NotNull(hover);
            }
        }
    }
}
