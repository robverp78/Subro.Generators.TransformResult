using Microsoft.CodeAnalysis;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System;

#nullable enable

namespace Subro.Generators
{

    /// <summary>
    /// Represents diagnostic information produced by an analysis, including the diagnostic descriptor, optional
    /// location, and message arguments.
    /// This is basically an instance of a DiagnosticDescriptor with the associated data needed to create a Diagnostic for reporting.
    /// </summary>
    /// <remarks>Use this struct to encapsulate all information needed to create and report a diagnostic from
    /// an analyzer. The contained data can be converted to a Diagnostic instance for reporting or further
    /// processing.</remarks>
    public readonly record struct DiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        LocationInfo? Location,
        params object[] MessageArgs
    ) : IDiagnosticInfoProvider
    {

        public DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location)
            : this(Descriptor, Location, []) { }

        /// <summary>
        /// Creates a new Diagnostic instance representing this analysis result.
        /// </summary>
        /// <remarks>Use this method to convert the current analysis context into a Diagnostic that can be
        /// reported by analyzers. The returned Diagnostic reflects the descriptor, location, and message arguments
        /// associated with this instance.</remarks>
        /// <returns>A Diagnostic object constructed from the current descriptor, location, and message arguments.</returns>
        public Diagnostic ToDiagnostic() => Diagnostic.Create(
            Descriptor,
            Location?.ToLocation(),
            MessageArgs);

        DiagnosticInfo IDiagnosticInfoProvider.CreateDiagnosticInfo() => this;

        /// <summary>
        /// Clones this <see cref="DiagnosticInfo"/> to a new instance with the new location.
        /// Useful if the same diagnostic needs to be reported in multiple locations, or if the location needs to be updated after the fact (e.g. after a transformation).
        /// </summary>
        public DiagnosticInfo NewLocation(LocationInfo Location) => new(Descriptor, Location, MessageArgs);

        /// <summary>
        /// A quick way to return a DiagnosticInfo from a DiagnosticDescriptor.
        /// Meant as a shortcut to return a DiagnosticInfo with no location and no message args, which can then be further modified with the other methods (e.g. NewLocation) or by creating a new DiagnosticInfo with the same descriptor and the desired data.
        /// </summary>
        /// <param name="descriptor"></param>
        public static implicit operator DiagnosticInfo(DiagnosticDescriptor descriptor)
            => new(descriptor, null);

        #region Equality. overrides equality to compare the message arguments contents (if any)
        public bool Equals(DiagnosticInfo other)
            => Descriptor.Equals(other.Descriptor)
            && Location == other.Location
            && MessageArgsEqual(MessageArgs, other.MessageArgs);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Descriptor?.GetHashCode() ?? 0;
                hash = hash * 31 + (Location?.GetHashCode() ?? 0);
                hash = hash * 31 + (MessageArgs?.Length ?? 0);
                return hash;
            }
        }

        static bool MessageArgsEqual(object[] a, object[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!Equals(a[i], b[i])) return false;
            return true;
        }

        #endregion
    }

    /// <summary>
    /// For metadata, <see cref="DiagnosticInfo"/> is the end result (until a Diagnostic is created), but
    /// builders can use <see cref="IDiagnosticInfoProvider"/> to allow for more flexible construction of diagnostics, 
    /// without having to create the DiagnosticInfo until it's needed.
    /// </summary>
    public interface IDiagnosticInfoProvider
    {
        DiagnosticInfo CreateDiagnosticInfo();
    }

    public record DiagnosticInfoProvider(DiagnosticDescriptorInfo DescriptorInfo, LocationInfo? Location, params object[] MessageArgs) : IDiagnosticInfoProvider
    {
        public DiagnosticInfo CreateDiagnosticInfo() => new(DescriptorInfo.GetDescriptor(), Location, MessageArgs);
    }

    [Obsolete("Use DiagnosticInfoProvider (without the extra s) instead. This is just for backward compatibility and will be removed in a future version.")]
    public record DiagnosticsInfoProvider(DiagnosticDescriptorInfo DescriptorInfo, LocationInfo? Location, params object[] MessageArgs) : IDiagnosticInfoProvider
    {
        public DiagnosticInfo CreateDiagnosticInfo() => new(DescriptorInfo.GetDescriptor(), Location, MessageArgs);
    }


    /// <summary>
    /// An easy way to create descriptor objects for diagnostics without having to create a static field for each one.
    /// Once the <see cref="DiagnosticDescriptor"/> is needed, a static reference is created or reused.
    /// </summary>
    public record DiagnosticDescriptorInfo
        (string ID, string MessageFormat,
        DiagnosticSeverity Severity)
    {
        public string? Category { get; init; }
        public string? Title { get; init; }

        static readonly ConcurrentDictionary<DiagnosticDescriptorInfo, DiagnosticDescriptor> DescriptorCache = [];

        public DiagnosticDescriptor GetDescriptor() => DescriptorCache.GetOrAdd(this, static v => v.CreateDescriptor());

        DiagnosticDescriptor CreateDescriptor() => new(
            id: ID,
            title: Title ?? ID,
            messageFormat: MessageFormat,
            category: Category ?? "Generator Errors",
            defaultSeverity: Severity,
            isEnabledByDefault: true);

        public DiagnosticInfoProvider CreateDiagnosticInfo(LocationInfo? Location, params object[] messageArgs)
            => new(this, Location, messageArgs);
    }


    /// <summary>
    /// General base for keeping diagnostic info (via a <see cref="DiagnosticsList"/>) Used for builders, not for transform results
    /// </summary>
    public class DiagnosticsContainer
    {
        DiagnosticsList? diagnosticsList;
        public DiagnosticsList Diagnostics => diagnosticsList ??= [];

        public bool HasDiagnostics => diagnosticsList != null && !diagnosticsList.IsEmpty;

        public ImmutableArray<DiagnosticInfo> GetDiagnosticInfos()
            => HasDiagnostics ? diagnosticsList!.GetDiagnosticInfos() : ImmutableArray<DiagnosticInfo>.Empty;
    }

    /// <summary>
    /// A builder list for building diagnostic info, optimized for the common case of having either no diagnostics or a single diagnostic, but can handle multiple diagnostics if needed.
    /// </summary>
    /// <remarks>
    /// The list is meant for keeping track and building diagnostics during analysis and transformations. 
    /// While 'building' (for example building a transform object), diagnostics are added in the form of a
    /// IDiagnosticInfoProvider, where the diagnostic info can be added in any shape is convenient.
    /// After that <see cref="GetDiagnosticInfos"/> can be used to get the list of DiagnosticInfo to
    /// get an immutable array of DiagnosticInfo, which can then be converted to Diagnostics for reporting in a later step.
    /// </remarks>
    public class DiagnosticsList : IReadOnlyCollection<IDiagnosticInfoProvider>
    {
        //this construction was made over just using a list because the expectation is it will be mostly either no or a single diagnostic
        IDiagnosticInfoProvider? firstEntry;
        List<IDiagnosticInfoProvider>? Overflow;

        public void Add(IDiagnosticInfoProvider d)
        {
            if (firstEntry is null) { firstEntry = d; return; }
            (Overflow ??= [firstEntry]).Add(d);
        }

        /// <summary>
        /// In case diagnostics are being added in a way where the first one is the most important: this method
        /// ensures that the given diagnostic is placed as the first entry
        /// </summary>
        public void SetMain(IDiagnosticInfoProvider diagnostic)
        {
            if (firstEntry is null) { firstEntry = diagnostic; return; }
           
            if (Overflow is null)
                Overflow = [diagnostic, firstEntry];
            else
                Overflow.Insert(0, diagnostic);
            firstEntry = diagnostic; //doesn't really matter since the main diagnostic is always the first entry, but just to be sure
        }

        public IEnumerable<IDiagnosticInfoProvider> All =>
            Overflow ?? (firstEntry == null ? [] : [firstEntry]);

        public bool IsEmpty => firstEntry == null;

        public int Count => firstEntry == null ? 0 : Overflow?.Count ?? 1;

        public void AddRange(IEnumerable<IDiagnosticInfoProvider> diagnostics)
        {
            foreach (var d in diagnostics)
                Add(d);
        }

        public void AddRange(IEnumerable<DiagnosticInfo> diagnostics, LocationInfo NewLocation)
        {
            foreach (var d in diagnostics)
                Add(d.NewLocation(NewLocation));
        }

        public void Add(DiagnosticsContainer container)
        {
            if (container.HasDiagnostics)
                AddRange(container.Diagnostics);
        }

        /// <summary>
        /// Convert the contents to an immutable array of DiagnosticInfo, which can then be converted to Diagnostics for reporting in a later step.
        /// </summary>
        /// <returns></returns>
        public ImmutableArray<DiagnosticInfo> GetDiagnosticInfos()
        {
            if (firstEntry == null) return ImmutableArray<DiagnosticInfo>.Empty;
            if (Overflow == null) return ImmutableArray.Create(firstEntry.CreateDiagnosticInfo());
            return Overflow.Select(static d => d.CreateDiagnosticInfo()).ToImmutableArray();
        }

        public void Clear()
        {
            Overflow = null;
            firstEntry = null;
        }

        IEnumerator<IDiagnosticInfoProvider> IEnumerable<IDiagnosticInfoProvider>.GetEnumerator() => All.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => All.GetEnumerator();


    }

    public static partial class Diagnostics
    {
        /// <summary>
        /// Create an 'instance' of a diagnostic info from a descriptor, with the given location and message args.
        /// </summary>
        public static DiagnosticInfo CreateInfo(this DiagnosticDescriptor descriptor, LocationInfo? location, params object[] messageArgs)
                => new(descriptor, location, messageArgs);

        /// <summary>
        /// Create an 'instance' of a diagnostic info from a descriptor, with the first location of the symbol and the given message args.
        /// </summary>
        public static DiagnosticInfo CreateInfoFromFirstLocation(this DiagnosticDescriptor descriptor, ISymbol symbol, params object[] messageArgs)
        => new(descriptor, LocationInfo.From(symbol), messageArgs);

        /// <summary>
        /// Creates an immutable array of diagnostic information for all the locations of the specified symbol, using the provided diagnostic
        /// descriptor and message arguments.
        /// </summary>
        /// <param name="descriptor">The diagnostic descriptor that defines the diagnostic's ID, message, and severity.</param>
        /// <param name="symbolForLocation">The symbol whose locations are used to associate with the generated diagnostics. If the symbol has no
        /// locations, the diagnostics will not be location-specific.</param>
        /// <param name="messageArgs">An array of arguments to format the diagnostic message. Can be empty if the message does not require
        /// formatting.</param>
        /// <returns>An immutable array of DiagnosticInfo instances, each associated with a location from the specified symbol.
        /// If the symbol has no locations, a single DiagnosticInfo without a location is returned.</returns>
        public static ImmutableArray<DiagnosticInfo> CreateInfos(this DiagnosticDescriptor descriptor, ISymbol symbolForLocation, params object[] messageArgs)
            => symbolForLocation.Locations.Length == 0
                ? ImmutableArray.Create(new DiagnosticInfo(descriptor, null, messageArgs))
                : symbolForLocation.Locations.Select(l => new DiagnosticInfo(descriptor, l, messageArgs)).ToImmutableArray();

        public static DiagnosticDescriptorInfo Descriptor(this DiagnosticSeverity severity, string id, string messageFormat)
             => new(id, messageFormat, severity);

        public static DiagnosticDescriptorInfo Error(string id, string messageFormat)
            => DiagnosticSeverity.Error.Descriptor(id, messageFormat);

        public static DiagnosticDescriptorInfo Warning(string id, string messageFormat)
            => DiagnosticSeverity.Warning.Descriptor(id, messageFormat);

        public static DiagnosticDescriptorInfo Info(string id, string messageFormat)
            => DiagnosticSeverity.Info.Descriptor(id, messageFormat);


        //prevented using extension{} blocks to lower language version requirements

        /// <summary>
        /// Adds the diagnostics as output (if any)
        /// </summary>
        public static void RegisterDiagnosticsOutput<T>(
            this IncrementalGeneratorInitializationContext context,
            IncrementalValuesProvider<TransformResult<T>> transformResults)
            => RegisterDiagnosticsOutput(context, transformResults.SelectMany(static (tr, _) => tr.Diagnostics));

        /// <summary>
        /// Adds the diagnostics of multiple results as output (if any)
        /// </summary>
        public static void RegisterDiagnosticsOutput<T>(
            this IncrementalGeneratorInitializationContext context,
            IncrementalValuesProvider<IEnumerable<TransformResult<T>>> transformResults)
            => RegisterDiagnosticsOutput(context, transformResults
                .SelectMany(static (trs, _) => trs.SelectMany(static (tr, _) => tr.Diagnostics)));

        /// <summary>
        /// Reports all collected diagnostics to the specified source production context.
        /// </summary>
        public static void RegisterDiagnosticsOutput(
            this IncrementalGeneratorInitializationContext context,
            IncrementalValuesProvider<DiagnosticInfo> diagnostics)
        {
            context.RegisterSourceOutput(diagnostics,
                 static (ctx, diagn) =>
                 {
                     ctx.ReportDiagnostic(diagn.ToDiagnostic());
                 });
        }
    }
}
