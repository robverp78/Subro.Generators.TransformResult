using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

#nullable enable

namespace Subro.Generators
{
    /// <summary>
    /// Stores <see cref="Location"/>s for use in metadata (replacement for Location to prevent storing symbol information)
    /// </summary>
    public readonly partial record struct LocationInfo(
        string? FilePath,
        TextSpan TextSpan,
        LinePositionSpan LineSpan
    )
    {
        /// <summary>
        /// Create a <see cref="LocationInfo"/> from a <see cref="Location"/> 
        /// </summary>
        public LocationInfo(Location location) : this(
                    location.SourceTree?.FilePath,
                    location.SourceSpan,
                    location.GetLineSpan().Span){ }

        /// <summary>
        /// Default empty <see cref="LocationInfo"/> object
        /// </summary>
        public static readonly LocationInfo None = new(Location.None);
        public LocationInfo():this(null,None.TextSpan, None.LineSpan)
        {
            
        }

        /// <summary>
        /// Create a <see cref="LocationInfo"/> from the FIRST available location of the symbol
        /// </summary>
        /// <param name="symbol"></param>
        public LocationInfo(ISymbol symbol):this(
            symbol.Locations.Length == 0 ? Location.None : symbol.Locations[0]) { }



        /// <summary>
        /// Create a <see cref="LocationInfo"/> from the first available location 
        /// </summary>
        public static LocationInfo From(in ImmutableArray<Location> locations) =>
            locations.Length == 0 ? None : locations[0];

        /// <summary>
        /// Create a <see cref="LocationInfo"/> from the location of the attribute's syntax reference
        /// </summary>
        public static LocationInfo From(AttributeData attribute, CancellationToken token = default)
            => attribute.ApplicationSyntaxReference
                    ?.GetSyntax(token)
                    .GetLocation() ?? None;

        /// <summary>
        /// Create a <see cref="LocationInfo"/> array from all the given locations
        /// </summary>
        /// <remarks>
        /// Since this is mainly based on a symbol and meant to built upon, an 
        /// emptry location array will give back a single entry with <see cref="None"/> 
        /// to avoid having to check for empty arrays when using the result.
        /// </remarks>
        public static ImmutableArray<LocationInfo> FromAll(in ImmutableArray<Location> locations)
            => locations.Length == 0
            ? ImmutableArray.Create(None)
            : locations.Select(static l => (LocationInfo)l).ToImmutableArray();

        /// <summary>
        /// Create a <see cref="LocationInfo"/> array from all the given locations
        /// </summary>
        public static ImmutableArray<LocationInfo> FromAll(ISymbol symbol)
            => FromAll(symbol.Locations);

        /// <summary>
        /// Create a <see cref="LocationInfo"/> from the first available location of the symbol
        /// </summary>
        public static LocationInfo From(ISymbol symbol)
            => From(symbol.Locations);

        /// <summary>
        /// Create a <see cref="LocationInfo"/> from a <see cref="SyntaxNode"/>.
        /// </summary>
        public static LocationInfo From(SyntaxNode node)
             => node.GetLocation();

        /// <summary>
        /// Navigates to the method name within a call expression by traversing child nodes.
        /// For member access (e.g. <c>obj.Method(args)</c>), returns the location of the method name.
        /// For simple calls (e.g. <c>Method(args)</c>), returns the location of the identifier.
        /// </summary>
        /// <remarks>
        /// This uses <see cref="SyntaxNode"/> navigation to avoid a dependency on language-specific syntax types.
        /// As a result, for generic calls (e.g. <c>Method&lt;T&gt;(args)</c>), the returned span covers the
        /// full name including type arguments, rather than just the identifier token.
        /// </remarks>        
        public static LocationInfo FromCaller(SyntaxNode invocation)
        {
            SyntaxNode? targetNode = null;

            foreach (var node in invocation.ChildNodes())
            {
                targetNode = node;
                break;
            }

            if (targetNode is null)
                return new LocationInfo(invocation.GetLocation());


            SyntaxNode? lastChild = null;
            foreach (var child in targetNode.ChildNodes())
            {
                lastChild = child; // Keep overwriting until we have the last one (preventing Linq)
            }

            // Return location of last child, or the expression itself
            return new LocationInfo(lastChild?.GetLocation() ?? targetNode.GetLocation());
        }

        /// <summary>
        /// Converts the location to a <see cref="Location"/>
        /// </summary>
        public Location ToLocation() =>
            this == None ? Location.None : Location.Create(FilePath ?? string.Empty, TextSpan, LineSpan);

        public static implicit operator LocationInfo(Location location) =>
                 location.Kind == LocationKind.None
                ? None
                : new(location);

    }
}
