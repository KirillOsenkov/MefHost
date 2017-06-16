using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Composition;

namespace MEFHost
{
    class Program
    {
        static readonly Resolver StandardResolver = Resolver.DefaultInstance;
        static readonly PartDiscovery Discovery = PartDiscovery.Combine(
            new AttributedPartDiscoveryV1(StandardResolver),
            new AttributedPartDiscovery(StandardResolver, true));

        public RuntimeComposition RuntimeComposition { get; private set; }
        public IExportProviderFactory ExportProviderFactory { get; private set; }
        public ExportProvider ExportProvider { get; private set; }

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            //AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;

            if (args.Length == 1 && Directory.Exists(args[0]))
            {
                Environment.CurrentDirectory = Path.GetFullPath(args[0]);
            }

            var sw = Stopwatch.StartNew();
            new Program().InitializeInstanceAsync().Wait();
            Console.WriteLine(sw.Elapsed.ToString("s\\.fff"));
        }

        static readonly string EntryPoint = Process.GetCurrentProcess().MainModule.FileName;
        static readonly string EntryPointDirectory = Path.GetDirectoryName(EntryPoint);

        private static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.IsDynamic)
            {
                return;
            }

            var location = args.LoadedAssembly.Location;
            location = Path.GetFullPath(location);
            if (File.Exists(location))
            {
                var localCopy = Path.Combine(EntryPointDirectory, Path.GetFileName(location));
                if (!File.Exists(localCopy))
                {
                    File.Copy(location, localCopy);
                }
            }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var simpleName = args.Name;
            int comma = simpleName.IndexOf(',');
            if (comma > -1)
            {
                simpleName = simpleName.Substring(0, comma);
            }

            var filePath = RudimentaryResolve(simpleName);
            if (filePath != null)
            {
                return AssemblyLoad(filePath);
            }

            return null;
        }

        async Task InitializeInstanceAsync()
        {
            ComposableCatalog catalog = ComposableCatalog.Create(StandardResolver)
                .WithCompositionService()
                .WithDesktopSupport();

            var assemblies = new HashSet<Assembly>();
            ReadAssembliesFromAddins(assemblies);

            // spawn discovery tasks in parallel for each assembly
            var tasks = new List<Task<DiscoveredParts>>(assemblies.Count);
            foreach (var assembly in assemblies)
            {
                var task = Task.Run (() => Discovery.CreatePartsAsync(assembly));
                // var task = Discovery.CreatePartsAsync(assembly);
                tasks.Add(task);
            }

            //await Task.WhenAll(tasks);

            //while (tasks.Count > 0)
            //{
            //    var task = await Task.WhenAny(tasks);
            //    catalog = catalog.AddParts(task.Result);
            //    tasks.Remove(task);
            //}

            foreach (var task in tasks)
            {
                catalog = catalog.AddParts(await task);
            }

            var discoveryErrors = catalog.DiscoveredParts.DiscoveryErrors;
            if (!discoveryErrors.IsEmpty)
            {
                throw new ApplicationException($"Catalog scanning errors encountered.\n{string.Join("\n", discoveryErrors)}");
            }

            CompositionConfiguration configuration = CompositionConfiguration.Create(catalog);

            if (!configuration.CompositionErrors.IsEmpty)
            {
                // capture the errors in an array for easier debugging
                var errors = configuration.CompositionErrors.ToArray();
                configuration.ThrowOnErrors();
            }

            RuntimeComposition = RuntimeComposition.CreateRuntimeComposition(configuration);
            ExportProviderFactory = RuntimeComposition.CreateExportProviderFactory();
            ExportProvider = ExportProviderFactory.CreateExportProvider();
        }

        static readonly string[] assembliesToCompose =
        {
            "Microsoft.CodeAnalysis.CSharp.Features",
            "Microsoft.CodeAnalysis.CSharp.Workspaces",
            "Microsoft.CodeAnalysis.Features",
            "Microsoft.CodeAnalysis.Workspaces",
            "Microsoft.CodeAnalysis.VisualBasic.Features",
            "Microsoft.CodeAnalysis.VisualBasic.Workspaces",
            "Microsoft.VisualStudio.Text.Logic",
            "MonoDevelop.Ide",
            "MonoDevelop.SourceEditor",
        };

        void ReadAssembliesFromAddins(HashSet<Assembly> assemblies)
        {
            foreach (var assemblyName in assembliesToCompose)
            {
                var assemblyFilePath = RudimentaryResolve(assemblyName);
                if (assemblyFilePath == null)
                {
                    Console.WriteLine("Can't resolve assembly: " + assemblyName);
                    Environment.Exit(1);
                }

                var assembly = AssemblyLoad(assemblyFilePath);
                assemblies.Add(assembly);
            }
        }

        private static string RudimentaryResolve(string assemblyName)
        {
            if (File.Exists(assemblyName))
            {
                return Path.GetFullPath(assemblyName);
            }

            foreach (var file in Directory.EnumerateFiles(Environment.CurrentDirectory, assemblyName + ".dll", SearchOption.AllDirectories))
            {
                return file;
            }

            return null;
        }

        public static Assembly AssemblyLoad(string asmPath)
        {
            if (Type.GetType("Mono.Runtime") == null)
            {
                // MEF composition under Win32 requires that all assemblies be loaded in the
                // Assembly.Load() context so use Assembly.Load() after getting the AssemblyName
                // (which, on Win32, also contains the full path information so Assembly.Load()
                // will work).
                var asmName = AssemblyName.GetAssemblyName(asmPath);
                return Assembly.Load(asmName);
            }

            return Assembly.LoadFrom(asmPath);
        }
    }
}
