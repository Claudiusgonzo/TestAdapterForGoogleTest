﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GoogleTestAdapter.Dia;
using GoogleTestAdapter.Helpers;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace GoogleTestAdapter
{
    [DefaultExecutorUri(GoogleTestExecutor.ExecutorUriString)]
    [FileExtension(".exe")]
    public class GoogleTestDiscoverer : AbstractOptionsProvider, ITestDiscoverer
    {
        private static readonly Regex CompiledTestFinderRegex = new Regex(Constants.TestFinderRegex, RegexOptions.Compiled);

        private static bool ProcessIdShown { get; set; } = false;

        public GoogleTestDiscoverer() : this(null) { }

        internal GoogleTestDiscoverer(AbstractOptions options) : base(options) { }

        public void DiscoverTests(IEnumerable<string> executables, IDiscoveryContext discoveryContext,
            IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            if (!ProcessIdShown)
            {
                ProcessIdShown = true;
                DebugUtils.CheckDebugModeForDiscoverageCode(logger);
            }

            List<string> googleTestExecutables = GetAllGoogleTestExecutables(executables, logger);
            VsTestFrameworkReporter reporter = new VsTestFrameworkReporter();
            foreach (string executable in googleTestExecutables)
            {
                List<TestCase> testCases = GetTestsFromExecutable(executable, logger);
                reporter.ReportTestsFound(discoverySink, testCases);
            }
        }

        internal List<TestCase> GetTestsFromExecutable(string executable, IMessageLogger logger)
        {
            List<string> consoleOutput = new ProcessLauncher(Options).GetOutputOfCommand(logger, "", executable, GoogleTestConstants.ListTestsOption, false, false, null, null);
            List<SuiteCasePair> suiteCasePairs = ParseTestCases(consoleOutput);
            suiteCasePairs.Reverse();
            List<SourceFileLocation> sourceFileLocations = GetSourceFileLocations(executable, suiteCasePairs, logger);

            logger.SendMessage(TestMessageLevel.Informational, "GTA: Found " + suiteCasePairs.Count + " tests in executable " + executable);

            List<TestCase> testCases = new List<TestCase>();
            foreach (SuiteCasePair suiteCasePair in suiteCasePairs)
            {
                testCases.Add(ToTestCase(executable, suiteCasePair, sourceFileLocations, logger));
                DebugUtils.LogDebugMessage(logger, TestMessageLevel.Informational, "GTA: Added testcase " + suiteCasePair.TestSuite + "." + suiteCasePair.TestCase);
            }
            return testCases;
        }

        private List<SuiteCasePair> ParseTestCases(List<string> output)
        {
            List<SuiteCasePair> suiteCasePairs = new List<SuiteCasePair>();
            string currentSuite = "";
            foreach (string line in output)
            {
                string trimmedLine = line.Trim('.', '\n', '\r');
                if (trimmedLine.StartsWith("  "))
                {
                    suiteCasePairs.Add(new SuiteCasePair
                    {
                        TestSuite = currentSuite,
                        TestCase = trimmedLine.Substring(2)
                    });
                }
                else
                {
                    string[] split = trimmedLine.Split(new[] { GoogleTestConstants.ParameterValueMarker }, StringSplitOptions.RemoveEmptyEntries);
                    currentSuite = split.Length > 0 ? split[0] : trimmedLine;
                }
            }

            return suiteCasePairs;
        }

        private List<SourceFileLocation> GetSourceFileLocations(string executable, List<SuiteCasePair> testcases, IMessageLogger logger)
        {
            List<string> symbols = testcases.Select(GetGoogleTestCombinedName).ToList();
            string SymbolFilterString = "*" + GoogleTestConstants.TestBodySignature;
            DiaResolver resolver = new DiaResolver(Options);
            return resolver.ResolveAllMethods(executable, symbols, SymbolFilterString, logger);
        }

        private string GetGoogleTestCombinedName(SuiteCasePair pair)
        {
            if (!pair.TestCase.Contains(GoogleTestConstants.ParameterizedTestMarker))
            {
                return GoogleTestConstants.GetTestMethodSignature(pair.TestSuite, pair.TestCase);
            }

            int index = pair.TestSuite.IndexOf('/');
            string suite = index < 0 ? pair.TestSuite : pair.TestSuite.Substring(index + 1);

            index = pair.TestCase.IndexOf('/');
            string testName = index < 0 ? pair.TestCase : pair.TestCase.Substring(0, index);

            return GoogleTestConstants.GetTestMethodSignature(suite, testName);
        }

        private TestCase ToTestCase(string executable, SuiteCasePair suiteCasePair, List<SourceFileLocation> sourceFileLocations, IMessageLogger logger)
        {
            string displayName = suiteCasePair.TestSuite + "." + suiteCasePair.TestCase;
            string symbolName = GetGoogleTestCombinedName(suiteCasePair);

            foreach (SourceFileLocation location in sourceFileLocations)
            {
                if (location.Symbol.Contains(symbolName))
                {
                    TestCase testCase = new TestCase(displayName, new Uri(GoogleTestExecutor.ExecutorUriString), executable)
                    {
                        DisplayName = displayName,
                        CodeFilePath = location.Sourcefile,
                        LineNumber = (int)location.Line
                    };
                    testCase.Traits.AddRange(GetTraits(testCase.FullyQualifiedName, location.Traits));
                    return testCase;
                }
            }
            logger.SendMessage(TestMessageLevel.Warning, "GTA: Could not find source location for test " + displayName);
            return new TestCase(displayName, new Uri(GoogleTestExecutor.ExecutorUriString), executable)
            {
                DisplayName = displayName
            };
        }

        private IEnumerable<Trait> GetTraits(string fullyQualifiedName, List<Trait> traits)
        {
            foreach (RegexTraitPair pair in Options.TraitsRegexesBefore.Where(p => Regex.IsMatch(fullyQualifiedName, p.Regex)))
            {
                if (!traits.Exists(T => T.Name == pair.Trait.Name))
                {
                    traits.Add(pair.Trait);
                }
            }

            foreach (RegexTraitPair pair in Options.TraitsRegexesAfter.Where(p => Regex.IsMatch(fullyQualifiedName, p.Regex)))
            {
                bool replacedTrait = false;
                foreach (Trait traitToModify in traits.ToArray().Where(T => T.Name == pair.Trait.Name))
                {
                    replacedTrait = true;
                    traits.Remove(traitToModify);
                    if (!traits.Contains(pair.Trait))
                    {
                        traits.Add(pair.Trait);
                    }
                }
                if (!replacedTrait)
                {
                    traits.Add(pair.Trait);
                }
            }
            return traits;
        }

        internal bool IsGoogleTestExecutable(string executable, IMessageLogger logger, string customRegex = "")
        {
            bool matches;
            string regexUsed;
            if (string.IsNullOrWhiteSpace(customRegex))
            {
                regexUsed = Constants.TestFinderRegex;
                matches = CompiledTestFinderRegex.IsMatch(executable);
            }
            else
            {
                regexUsed = customRegex;
                try
                {
                    matches = Regex.IsMatch(executable, customRegex);
                }
                catch (ArgumentException e)
                {
                    logger.SendMessage(TestMessageLevel.Error,
                        "GTA: Regex '" + regexUsed + "' configured under Options/Google Test Adapter can not be parsed: " + e.Message);
                    matches = false;
                }
                catch (RegexMatchTimeoutException e)
                {
                    logger.SendMessage(TestMessageLevel.Error,
                        "GTA: Regex '" + regexUsed + "' configured under Options/Google Test Adapter timed out: " + e.Message);
                    matches = false;
                }
            }

            DebugUtils.LogUserDebugMessage(logger, Options, TestMessageLevel.Informational,
                    "GTA: " + executable + (matches ? " matches " : " does not match ") + "regex '" + regexUsed + "'");

            return matches;
        }

        private List<string> GetAllGoogleTestExecutables(IEnumerable<string> allExecutables, IMessageLogger logger)
        {
            return allExecutables.Where(e => IsGoogleTestExecutable(e, logger, Options.TestDiscoveryRegex)).ToList();
        }


        class SuiteCasePair
        {
            public string TestSuite;
            public string TestCase;
        }

        public class SourceFileLocation
        {
            public string Symbol;
            public string Sourcefile;
            public uint Line;
            public List<Trait> Traits;
        }

    }

}