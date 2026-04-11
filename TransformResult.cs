using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Linq;

#nullable enable

namespace Subro.Generators
{

    /// <summary>
    /// Lightweight struct to store the result of a transformation in a syntax provider, 
    /// along with any diagnostics that should be emitted.
    /// It can have either a Result (T) or Diagnostics, or both, or neither.
    /// </summary>
    public readonly record struct TransformResult<T>(T? Result, ImmutableArray<DiagnosticInfo> Diagnostics)
    {
        /// <summary>
        /// Transform result with only a Result (T) and no diagnostics.
        /// </summary>
        public TransformResult(T Result) : this(Result, ImmutableArray<DiagnosticInfo>.Empty) { }

        /// <summary>
        /// Transform result with only Diagnostics and no Result (T).
        /// </summary>
        public TransformResult(ImmutableArray<DiagnosticInfo> Diagnostics) : this(default, Diagnostics) { }

        /// <summary>
        /// Transform result with only Diagnostics and no Result (T).
        /// </summary>
        public TransformResult(IEnumerable<DiagnosticInfo> Diagnostics) : this(ImmutableArray.CreateRange(Diagnostics)) { }

        /// <summary>
        /// Transform result with a single Diagnostic and no Result (T).
        /// </summary>
        public TransformResult(DiagnosticInfo Diagnostic) : this(ImmutableArray.Create(Diagnostic)) { }

        /// <summary>
        /// Transform result with a single Diagnostic and no Result (T).
        /// </summary>
        public TransformResult(IDiagnosticInfoProvider Diagnostic) : this(ImmutableArray.Create(Diagnostic.CreateDiagnosticInfo())) { }

        /// <summary>
        /// Indicates if the TransformResult is empty, meaning it has no Result and no Diagnostics.
        /// </summary>
        public bool IsEmpty => Result == null && Diagnostics.Length == 0;

        /// <summary>
        /// Gets a value indicating whether any diagnostics are present.
        /// </summary>
        public bool HasDiagnostics => Diagnostics.Length > 0;

        public static implicit operator TransformResult<T>(in DiagnosticInfo diagnostic)
            => new (default, ImmutableArray.Create(diagnostic)); //shortcut to return an error

        public static implicit operator TransformResult<T>(DiagnosticInfoProvider diagnostic)
            => new(default, ImmutableArray.Create(diagnostic.CreateDiagnosticInfo())); //shortcut to return an error

        public static implicit operator TransformResult<T>(in ImmutableArray<DiagnosticInfo> diagnostics)
            => new(default, diagnostics);

        public static implicit operator TransformResult<T>(T Result)
            => new(Result, ImmutableArray<DiagnosticInfo>.Empty);

        #region Equality. overrides equality to compare the diagnostic contents (if any)
        public bool Equals(TransformResult<T> other)
            => EqualityComparer<T?>.Default.Equals(Result, other.Result)
            && Diagnostics.SequenceEqual(other.Diagnostics);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Result?.GetHashCode() ?? 0) * 31 + Diagnostics.Length;
            }
        }

        #endregion
    }


    /// <summary>
    /// Optional helper for building a <see cref="TransformResult{T}"/> where there can 
    /// be multiple paths and/or multiple diagnostics.
    /// </summary>
    /// <remarks>
    /// For most usages the implicit conversions for a <see cref="TransformResult{T}"/> 
    /// will be enough. This helper is only needed for scenarios where more diagnostics are needed
    /// </remarks>
    public class TransformResultBuilder<T>: DiagnosticsContainer
    {
        /// <summary>
        /// The resulting metadata for the transformation
        /// </summary>
        public T? Result { get; set; }

        /// <summary>
        /// Default method to return the transform result. (implicit conversion is also supported)
        /// </summary>
        /// <returns></returns>
        public TransformResult<T> GetResult() => new(Result, GetDiagnosticInfos());

        public static implicit operator TransformResult<T>(TransformResultBuilder<T> builder) => builder.GetResult();

        /// <summary>
        /// Short cut to make the given error the "main" error (only meaning the first diagnostic in the
        /// list, if more diagnostics were added)
        /// And returns those diagnostics without a result, even if the <see cref="Result"/> was set before.
        /// </summary>
        public TransformResult<T> FatalError(DiagnosticInfo error)
        {
            Diagnostics.SetMain(error);
            return Diagnostics.GetDiagnosticInfos();
        }
    }

    /// <summary>
    /// Helper function around transformations for generator functions.
    /// Mainly geared around <see cref="TransformResult{T}"/>.
    /// </summary>
    public static partial class Transform
    {
        /// <summary>
        /// Create a helper to build a <see cref="TransformResult{T}"/>
        /// </summary>
        public static TransformResultBuilder<T> Build<T>() => new();

        //prevented using extension{} blocks to lower language version requirements

        /// <summary>
        /// Simple filter on transform results to see if they contain either diagnostics or have a result (T)
        /// </summary>
        public static IncrementalValuesProvider<TransformResult<T>> WhereNotEmpty<T>(
            this IncrementalValuesProvider<TransformResult<T>> transformResults)
            => transformResults.Where(static t => !t.IsEmpty);

        /// <summary>
        /// Gets the Result (T) values where not empty
        /// </summary>
        public static IncrementalValuesProvider<T> GetResults<T>(
            this IncrementalValuesProvider<TransformResult<T>> transformResults)
            => transformResults.Where(static t => t.Result is not null).Select(static (t, _) => t.Result!);

        /// <summary>
        /// Combination of <see cref="RegisterDiagnosticsOutput"/> and <see cref="GetResults"/>
        /// Easiest way to finalize the outcome of the syntax provider and just get the (not empty) results of T
        /// </summary>
        public static IncrementalValuesProvider<T> RegisterDiagnosticsAndReturnResults<T>(
            this IncrementalValuesProvider<TransformResult<T>> transformResults,
            IncrementalGeneratorInitializationContext context)
        {
            context.RegisterDiagnosticsOutput(transformResults);
            return transformResults.Where(static t => t.Result is not null).Select(static (t, _) => t.Result!);
        }

        public static IncrementalValuesProvider<T> RegisterDiagnosticsAndReturnResults<T>(
            this IncrementalValuesProvider<IEnumerable<TransformResult<T>>> transformResults,
            IncrementalGeneratorInitializationContext context)
        {
            context.RegisterDiagnosticsOutput( transformResults);
            return transformResults
                .SelectMany(static (t,_) => 
                    t.Where(static t => t.Result is not null)
                    .Select(static (t, _) => t.Result!));
        }

        /// <summary>
        /// The fast track for creating a syntax provider. Call this instead of using CreateSyntaxProvider.
        /// Use the same predicate you would normally use, and have the transform
        /// function return <see cref="TransformResult{T}"/>. If you return diagnostics,
        /// they will be handled automatically, and you only get any result of the T
        /// back which have been set, and which you can use directly
        /// </summary>
        public static IncrementalValuesProvider<T> CreateValuesProvider<T>(
            this IncrementalGeneratorInitializationContext context,
            Func<SyntaxNode, CancellationToken, bool> predicate, 
            Func<GeneratorSyntaxContext, CancellationToken, TransformResult<T>> transform)
        {
            var provider = context.SyntaxProvider.CreateSyntaxProvider(predicate, transform);
            return provider.RegisterDiagnosticsAndReturnResults(context);
        }

        /// <summary>
        /// The fast track for creating a syntax provider. Call this instead of using CreateSyntaxProvider.
        /// Use the same predicate you would normally use, and have the transform
        /// function return an IEnumerable of <see cref="TransformResult{T}"/>. 
        /// If diagnostics are returned in any of the results,
        /// they will be handled automatically, and only the valid  results of T will be returned
        /// </summary>
        public static IncrementalValuesProvider<T> CreateValuesProvider<T>(
            this IncrementalGeneratorInitializationContext context,
            Func<SyntaxNode, CancellationToken, bool> predicate,
            Func<GeneratorSyntaxContext, CancellationToken, IEnumerable<TransformResult<T>>> transforms)
        {
            var provider = context.SyntaxProvider.CreateSyntaxProvider(predicate, transforms);
            return provider.RegisterDiagnosticsAndReturnResults(context);
        }

        /// <summary>
        /// Fast track for using <see cref="SyntaxValueProvider.ForAttributeWithMetadataName"/>.
        /// Use the same predicate you would normally use, and have the transform
        /// function return <see cref="TransformResult{T}"/>. If you return diagnostics,
        /// they will be handled automatically, and you only get any result of the T
        /// back which have been set, and which you can use directly
        /// </summary>
        public static IncrementalValuesProvider<T> ValuesForAttributeWithMetadataName<T>(
            this IncrementalGeneratorInitializationContext context,
            string fullyQualifiedMetadataName,
            Func<SyntaxNode, CancellationToken, bool> predicate,
            Func<GeneratorAttributeSyntaxContext, CancellationToken, TransformResult<T>> transform)
        {
            var provider = context.SyntaxProvider.ForAttributeWithMetadataName(fullyQualifiedMetadataName, predicate, transform);
            return provider.RegisterDiagnosticsAndReturnResults(context);
        }

        /// <summary>
        /// Fast track for using <see cref="SyntaxValueProvider.ForAttributeWithMetadataName"/>.
        /// Use the same predicate you would normally use, and have the transform
        /// function return an IEnumerable of <see cref="TransformResult{T}"/>. 
        /// If diagnostics are returned in any of the results,
        /// they will be handled automatically, and only the valid  results of T will be returned
        /// </summary>
        public static IncrementalValuesProvider<T> ValuesForAttributeWithMetadataName<T>(
            this IncrementalGeneratorInitializationContext context,
            string fullyQualifiedMetadataName,
            Func<SyntaxNode, CancellationToken, bool> predicate,
            Func<GeneratorAttributeSyntaxContext, CancellationToken, IEnumerable< TransformResult<T>>> transforms)
        {
            var provider = context.SyntaxProvider.ForAttributeWithMetadataName(fullyQualifiedMetadataName, predicate, transforms);
            return provider.RegisterDiagnosticsAndReturnResults(context);
        }

    }
}
