﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dotnet.Script.DependencyModel.Context;
using Dotnet.Script.DependencyModel.Environment;
using Dotnet.Script.DependencyModel.Logging;
using Dotnet.Script.DependencyModel.Process;
using Dotnet.Script.DependencyModel.ProjectSystem;
using Dotnet.Script.DependencyModel.ScriptPackage;
using Microsoft.Extensions.DependencyModel;

namespace Dotnet.Script.DependencyModel.Runtime
{
    public class RuntimeDependencyResolver
    {
        private readonly ScriptProjectProvider _scriptProjectProvider;
        private readonly ScriptDependencyInfoProvider _scriptDependencyInfoProvider;
        private readonly ScriptFilesDependencyResolver _scriptFilesDependencyResolver;
        private readonly Logger _logger;
        private readonly ScriptEnvironment _scriptEnvironment;
        private readonly Regex _runtimeMatcher;

        private RuntimeDependencyResolver(ScriptProjectProvider scriptProjectProvider, ScriptDependencyInfoProvider scriptDependencyInfoProvider, ScriptFilesDependencyResolver scriptFilesDependencyResolver, LogFactory logFactory, ScriptEnvironment scriptEnvironment, bool useRestoreCache)
        {
            _scriptProjectProvider = scriptProjectProvider;
            _scriptDependencyInfoProvider = scriptDependencyInfoProvider;
            _scriptFilesDependencyResolver = scriptFilesDependencyResolver;
            _logger = logFactory.CreateLogger<RuntimeDependencyResolver>();
            _scriptEnvironment = scriptEnvironment;
            _runtimeMatcher = new Regex($"{_scriptEnvironment.PlatformIdentifier}.*-{_scriptEnvironment.ProccessorArchitecture}");
        }

        public RuntimeDependencyResolver(LogFactory logFactory, bool useRestoreCache)
            : this
            (
                  new ScriptProjectProvider(logFactory),
                  new ScriptDependencyInfoProvider(CreateRestorer(logFactory, useRestoreCache), logFactory),
                  new ScriptFilesDependencyResolver(logFactory),
                  logFactory,
                  ScriptEnvironment.Default,
                  useRestoreCache
            )
        {
        }

        private static IRestorer CreateRestorer(LogFactory logFactory, bool useRestoreCache)
        {
            var commandRunner = new CommandRunner(logFactory);
            if (useRestoreCache)
            {
                return new ProfiledRestorer(new CachedRestorer(new DotnetRestorer(commandRunner, logFactory),logFactory),logFactory);
            }
            else
            {
                return new ProfiledRestorer(new DotnetRestorer(commandRunner, logFactory),logFactory);
            }

        }

        public IEnumerable<RuntimeDependency> GetDependencies(string targetDirectory, ScriptMode scriptMode, string[] packagesSources, string code = null)
        {
            var pathToProjectFile = scriptMode == ScriptMode.Script
                ? _scriptProjectProvider.CreateProject(targetDirectory, _scriptEnvironment.TargetFramework, true)
                : _scriptProjectProvider.CreateProjectForRepl(code, Path.Combine(targetDirectory, scriptMode.ToString()), ScriptEnvironment.Default.TargetFramework);

            return GetDependenciesInternal(pathToProjectFile, packagesSources);
        }

        public IEnumerable<RuntimeDependency> GetDependencies(string scriptFile, string[] packagesSources)
        {
            var pathToProjectFile = _scriptProjectProvider.CreateProjectForScriptFile(scriptFile);
            return GetDependenciesInternal(pathToProjectFile, packagesSources);
        }

        public IEnumerable<RuntimeDependency> GetDependencies(string dllPath)
        {
            return GetDependenciesInternal(dllPath, restorePackages: false);
        }

        private IEnumerable<RuntimeDependency> GetDependenciesInternal(string pathToProjectOrDll, string[] packageSources = null, bool restorePackages = true)
        {
            packageSources = packageSources ?? new string[0];
            ScriptDependencyInfo dependencyInfo;
            if (restorePackages)
            {
                dependencyInfo = _scriptDependencyInfoProvider.GetDependencyInfo(pathToProjectOrDll, packageSources);
            }
            else
            {
                dependencyInfo = _scriptDependencyInfoProvider.GetDependencyInfo(pathToProjectOrDll);
            }

            var dependencyContext = dependencyInfo.DependencyContext;
            List<string> nuGetPackageFolders = dependencyInfo.NugetPackageFolders.ToList();
            nuGetPackageFolders.Add(_scriptEnvironment.NuGetStoreFolder);

            var runtimeDependencies = new List<RuntimeDependency>();

            var runtimeLibraries = dependencyContext.RuntimeLibraries;

            foreach (var runtimeLibrary in runtimeLibraries)
            {
                var runtimeDependency = new RuntimeDependency(runtimeLibrary.Name, runtimeLibrary.Version,
                    ProcessRuntimeAssemblies(runtimeLibrary, nuGetPackageFolders.ToArray()),
                    ProcessNativeLibraries(runtimeLibrary, nuGetPackageFolders.ToArray()),
                    ProcessScriptFiles(runtimeLibrary, nuGetPackageFolders.ToArray(), restorePackages));

                runtimeDependencies.Add(runtimeDependency);
            }

            return runtimeDependencies;
        }

        private string[] ProcessScriptFiles(RuntimeLibrary runtimeLibrary, string[] nugetPackageFolders, bool restorePackages)
        {
            if (restorePackages)
            {
                return _scriptFilesDependencyResolver.GetScriptFileDependencies(runtimeLibrary.Path, nugetPackageFolders);
            }
            else
            {
                // If restorePackages are false, it means we are running from a DLL
                // and the scripts from the script packages are already compiled into the DLL
                // NOTE: This whole class needs some cleanup.
                return Array.Empty<string>();
            }

        }

        private string[] ProcessNativeLibraries(RuntimeLibrary runtimeLibrary, string[] nugetPackageFolders)
        {
            List<string> result = new List<string>();
            foreach (var nativeLibraryGroup in runtimeLibrary.NativeLibraryGroups.Where(
                nlg => AppliesToCurrentRuntime(nlg.Runtime)))
            {

                foreach (var assetPath in nativeLibraryGroup.AssetPaths.Where(path => !path.EndsWith("_._")))
                {
                    var fullPath = GetFullPath(Path.Combine(runtimeLibrary.Path, assetPath), nugetPackageFolders);
                    _logger.Trace($"Loading native library from {fullPath}");
                    result.Add(fullPath);
                }
            }
            return result.ToArray();
        }
        private RuntimeAssembly[] ProcessRuntimeAssemblies(RuntimeLibrary runtimeLibrary, string[] nugetPackageFolders)
        {
            var result = new List<RuntimeAssembly>();

            var runtimeAssemblyGroup =
                runtimeLibrary.RuntimeAssemblyGroups.FirstOrDefault(rag =>
                    rag.Runtime == _scriptEnvironment.RuntimeIdentifier);

            if (runtimeAssemblyGroup == null)
            {
                runtimeAssemblyGroup =
                    runtimeLibrary.RuntimeAssemblyGroups.FirstOrDefault(rag => string.IsNullOrWhiteSpace(rag.Runtime));
            }
            if (runtimeAssemblyGroup == null)
            {
                return Array.Empty<RuntimeAssembly>();
            }
            foreach (var assetPath in runtimeAssemblyGroup.AssetPaths)
            {
                var path = Path.Combine(runtimeLibrary.Path, assetPath);
                if (!path.EndsWith("_._"))
                {
                    var fullPath = GetFullPath(path, nugetPackageFolders);

                    _logger.Trace($"Resolved runtime library {runtimeLibrary.Name} located at {fullPath}");
                    result.Add(new RuntimeAssembly(AssemblyName.GetAssemblyName(fullPath), fullPath));
                }
            }
            return result.ToArray();
        }

        private static string GetFullPath(string relativePath, IEnumerable<string> nugetPackageFolders)
        {
            foreach (var possibleLocation in nugetPackageFolders)
            {
                var fullPath = Path.Combine(possibleLocation, relativePath);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            string message = $@"The requested dependency ({relativePath}) was not found in the global Nuget cache(s).
. Try executing/publishing the script again with the '--no-cache' option";
            throw new InvalidOperationException(message);
        }

        private bool AppliesToCurrentRuntime(string runtime)
        {
            return string.IsNullOrWhiteSpace(runtime) || _runtimeMatcher.IsMatch(runtime);
        }
    }
}