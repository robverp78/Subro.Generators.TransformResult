# [Subro.Generators.TransformResult](https://www.nuget.org/packages/Subro.Generators.TransformResult/)

## What is it
Helps streamline incremental generator transfomrations and diagnostics

Only focus on your own metadata to generate code, instead of on the process of getting there.
Diagnostics (such as warnings or errors) are optional and can be registered automatically.
The aim of this library is to keep the generator clean, so it only has to focus on your own
metadata and code generation.

This is a very simple library really, but it saved me a lot of repetitive code and kept
my generators clean.

## NB

This is shared source for a generator. If you have the generator project in place, it probably
works as is, but be sure to look at the [Requirements](#requirements)

---

## Fast track examples

There are two main extension functions:
`context.ValuesForAttributeWithMetadataName` replaces `context.SyntaxProvider.ForAttributeWithMetadataName`
`context.CreateValuesProvider` replaces `context.SyntaxProvider.CreateSyntaxProvider`

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
    var results = context.ValuesForAttributeWithMetadataName<MyCreationInfo>(
        "MyAttribute", Predicate, TransformFunction); 

    context.RegisterSourceOutput(results, YourCodeGeneration);  
}

// results will only contain the valid results; if any diagnostics have been returned, they
// will have been handled by the library
```

The only thing you have to do is have your transform function return a `TransformResult<T>`, where `T`
is the type of your own metadata object.
The main advantage: you can just start with returning your own object, or `default` if it should not
be included, and add diagnostics later.
So you can start with just:

```csharp
// The transform function has to return TransformResult<T>. Whether it is static, or whether it includes
// the CancellationToken in the definition or ignores it during registration, does not matter.
static TransformResult<YourMetaDataObject> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{
    return new YourMetaDataObject();
}
```

So the only change is returning `TransformResult<YourMetaDataObject>` instead of `YourMetaDataObject?`,
and you still have the advantage that you do not have to check for valid values.

Then at any time you can expand (or do this right away of course):

```csharp
// The transform function has to return TransformResult<T>. Whether it is static, or whether it includes
// the CancellationToken in the definition or ignores it during registration, does not matter.
static TransformResult<YourMetaDataObject> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{
    if (someCheckFails)     // heavier checks than in the predicate, but still needed for your generator
        return default;     // this result will be filtered out automatically

    if (someFatalCondition)
        return Diagnostics.Error(...); // there are several helpers for this — more on that further below
                               // the error will be added to the output, but since there is no result,
                               // your generator does not have to deal with it

    if (someCheckIndicatesAWarning)
        return new(yourMetaDataObject, [warningOrWarnings]);
    
    // no diagnostics, just return the object
    return yourMetaDataObject;
}
```

So in short: if `default` (empty) is returned, it will be filtered out automatically.
If diagnostics are returned, they are handled in their own path.
The end result is only the valid objects.

For returning multiple TransformResults, see [this section](#return-multiple-transform-results)

## Requirements

- C# 12 or higher (e.g. `<LangVersion>12</LangVersion>` or `latest`)
- Roslyn 4.x (`Microsoft.CodeAnalysis.CSharp` 4.0+)

Both are standard for any project using incremental generators with Visual Studio 2022
or .NET SDK 6+. If you have a working incremental generator, you already meet these requirements.

If your project is defined as a generator, you probably already have `IsExternalInit` defined.
This is needed to be able to use elements such as `record`s and init-only properties in
`netstandard2.0` generator projects.
If you do not have it already, you can add a reference to the `IsExternalInit` NuGet package,
*or* add something like this to your project:

```csharp
#if !NET5_0_OR_GREATER 
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}
#endif
```

---

## Included

- `TransformResult<T>` — the core result object
- `LocationInfo` — stores locations without symbol information in metadata (mainly for diagnostics)
- Several `Diagnostic` helper objects. They are mainly made for using with TransformResult, but can be used separately.
- Several extension functions to help out when not using the fast track, but still using `TransformResult`
- `TransformResultBuilder<T>` — helps build a `TransformResult` when multiple paths and diagnostics may be needed

---

## The problem it solves

Writing incremental generators involves a lot of the same process. Some of it is of
course unavoidable, because it will be different for each generator. But a lot of
it is also recurring.

### Generator (with provider) recap

Just in case, for those relatively new to generators, a quick summary:
Before you generate your code, you have to gather the information (from symbols) that
you want to base your generated code on. A generator can emit static code of course,
but most of the time you need that information first. And mostly that means using
`SyntaxProvider.ForAttributeWithMetadataName` or `SyntaxProvider.CreateSyntaxProvider`.

For both, there is a predicate and a transform parameter. The predicate is meant to quickly filter
out symbols that are not applicable. It should be lightweight and as fast as
possible so as not to slow down the compiler.

The transform is where you convert that filtered data into enough information to
start writing the actual code. Which metadata you need for that depends on the goal
of your generator. What is important is that it is comparable, so the compiler (IDE)
can see if it has changed — and therefore whether the code generators that depend on
the returned values need to run at all. That means the metadata should be comparable
and avoid holding reference values. Diagnostic information should also be comparable
and avoid keeping symbols.

Why return diagnostics in the first place? Often while building the transform you will
encounter situations where you either cannot build the code and want to inform the user
of an error, or you can build the code but there are problems you want to warn the user about.
It is also important to realize that the transform is the extra filter: for every check that
is too heavy for the predicate, the transform can decide to return an empty or null
object to indicate that the code generator should not run for this symbol.

### Without this library

How you handle diagnostics and values is of course up to you. You might
return an object with the diagnostics and the result(s) as separate values, or an
object with optional diagnostics in it, return null if it is empty, an empty tuple,
or one of many other options.
You can then check for those diagnostics inside a separate pipeline, or check
whether the metadata object contains diagnostics.

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
    var results = context.SyntaxProvider
        .ForAttributeWithMetadataName("MyAttribute", Predicate, TransformFunction)
        .Where(static r => r is not null);

    // Diagnostics pipeline
    var diagnostics = results.Where(static r => r!.Diagnostics.Length > 0);
    context.RegisterSourceOutput(diagnostics, (spc, r) =>
    {
        foreach (var d in r!.Diagnostics)
            spc.ReportDiagnostic(d.ToDiagnostic());
    });

    // Generation pipeline
    var valid = results
        .Where(static r => r!.Result is not null)
        .Select(static (r, _) => r!.Result!); // ensures the provider emits non-null values

    context.RegisterSourceOutput(valid, YourCodeGeneration);
}

static YourResultObject? TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{
    // handle each situation, including keeping a local list of diagnostics or creating it on the fly
}

record YourResultObject(YourMetaDataObject? Result, ImmutableArray<DiagnosticInfo> Diagnostics)
{
    ...
} 

record YourMetaDataObject
{
   ...
}
// you will need to create your result object and diagnostic info classes as well
```

This is just one possible pipeline — there are many other options — but the checks remain regardless.
And often, at least for me, you start with just `YourMetaDataObject?` because you do not need
diagnostics yet, and it is overkill to design the full structure right then. Only to later find
out that diagnostics are needed and refactoring is required.

### With this library

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
    var results = context.ValuesForAttributeWithMetadataName<MyCreationInfo>(
        "MyAttribute", Predicate, TransformFunction);

    context.RegisterSourceOutput(results, YourCodeGeneration);
}

static TransformResult<YourMetaDataObject> TransformFunction(GeneratorAttributeSyntaxContext context, CancellationToken ct) 
{
    ...
}

record YourMetaDataObject
{
    ...
}
```

The diagnostics are handled automatically. Empty results are filtered. You just get the valid results of `T`.
By using this as the default setup, you can simply return `YourMetaDataObject` and add diagnostics later
if needed, without changing any of the surrounding definitions.

---

## Transform results

Your transform function returns a `TransformResult<T>`. The implicit operators make the common cases as concise as possible:

```csharp
static TransformResult<MyCreationInfo> Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken token)
{
    // Quick exit — no result, no diagnostics
    if (ctx.TargetNode is not InterfaceDeclarationSyntax interfaceNode)
        return default;

    // Return a fatal error — no result, one diagnostic
    if (someCondition)
        return Diagnostics.Error(...);
    
    // Return a successful result
    return new MyCreationInfo(...);

    //and several combinations between results, one diagnostic, multiple diagnostics
}
```

All these example cases are a single `return` statement. No builder required for simple transforms.

---

## Diagnostics

### Roslyn defaults

Roslyn uses reference equality for `DiagnosticDescriptor`s for diagnostic deduplication in the IDE. 
(of course, this and all other information in this document is to the best of my knowledge, and based
on the information at the time of writing). 
That means the default behavior for using DiagnosticDescriptor, is declaring them as static.

### Using the DiagnosticDescriptor with this library

Using existing static DiagnosticDescriptors is fully supported by the library. An 'instance' of such
a descriptor can be created, meaning the descriptor with information for that particular
location and information. So the location info, and the message argument.
For example the descriptor contains the general information, that it is a warning (severity level)
and the messageformat is `"Parameter {0} can not be null."`, the instance can be that descriptor
with `LocationInfo.From(IParameterSymbolVariable)` and a message argument `IParameterSymbolVariable.Name`.

All in all, that is the end result in this library: a `DiagnosticInfo` structure, that contains the
(reference to the static) Descriptor, an optional location, and the message arguments.

Assuming you have your own static DiagnosticDescriptor, you can create an 'instance' for reporting
with the extension function CreateInfo. For example:
 `YourDescriptorInstance.CreateInfo(new (IParameterSymbolVariable),IParameterSymbolVariable.Name)`

  So for a transform function, you can return a DiagnosticDescriptor directly (e.g. as an error shortcut)

```csharp
static TransformResult<YourMetaDataObject> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{
    if(somethingWrong)
        return YourDescriptorInstance.CreateInfo(new(IParameterSymbolVariable),IParameterSymbolVariable.Name);
}
```

Side note: like with the vanilla generator, if you want to use message arguments depends on the needs or circumstances.
If you have a DiagnosticDescriptor with a plain text messageformat (no arguments), you can simply use
`YourDescriptorInstance.CreateInfo(new (IParameterSymbolVariable))`

### About locations

`LocationInfo` is a cache-safe replacement for `Location`. Roslyn's `Location` holds a reference to the full `SyntaxTree`, 
which defeats incremental caching if stored in transform output. `LocationInfo` stores only the primitive data needed to reconstruct it.

 NB, `LocationInfo` has several `From` methods, including From(ISymbol) and for ease of use, it also has a constructor
 with symbol. But some symbols can have multiple locations (partial methods/class definitions/etc) The default From, as
 well as the constructor assumes the *first* location.
 Parameter and such symbols will only have a single location, but for the rest it is advisable to
 filter out the need for the correct location. There is also a `FromAll` method to get all locations, and
 an extension YourDescriptorInstance.`CreateInfos(symbol, ..messageArgs)` to get a DiagnosticInfo
 for all locations of that symbol. This is not a consequence of the library, but from locations in general.

To return multiple diagnostics you can return an immutable array of DiagnosticInfo, or return new (\[diagnostics\]).
Or with a result and diagnostics, return new (resultObject, \[diagnostics\])
There is also an option of using a DiagnosticsList to build diagnostics, or using a `TransformResultBuilder<T>` which has
a Diagnostics collection.

### Diagnostic helpers

For use in TransformResultBuilder or another DiagnosticsList or directly from within a transform function, there is a
`DiagnosticDescriptorInfo`, which is basically an instance wrapper, that can create a `static` `DiagnosticDescriptor`
when it is needed. 
The idea behind it, is that a lot of these diagnostics are expected to be exceptions. You might not want to create static
fields for all those exceptions, but just keep them in your code.
In that case you can create a DiagnosticDescriptorInfo directly, or with helpers for ease of use. A DiagnosticDescriptorInfo
contains at least an `ID` , a `MessageFormat`, and a severity (`DiagnosticSeverity`). And can optionally contain a `Title` and a `Category`.

From that DiagnosticDescriptorInfo, you can create a DiagnosticInfo directly.

```csharp
static TransformResult<YourMetaDataObject> TransformFunction(GeneratorAttributeSyntaxContext context, CancellationToken ct) 
{
    if(somethingWrong)
        //the DiagnosticDescriptorInfo is the main part. A static DiagnosticDescriptor will be created or used when the diagnostic is needed
        return new DiagnosticDescriptorInfo("Code001", "{0} had a little lamb", DiagnosticSeverity.Error) 
        { Title = "optional title", Category = "optional category" }
            //The instance. The location this diagnostics is needed for, and optional message arguments
            .CreateDiagnosticInfo(new LocationInfo(context.TargetSymbol),"A random sheep");
}
```

Of course that is still quite verbose. Which is why there are several helper functions to make that easier.
You can use extension functions on a DiagnosticSeverity value, or use the Diagnostics.Error/Warning/Info shortcuts

```csharp
static TransformResult<YourMetaDataObject> TransformFunction(GeneratorAttributeSyntaxContext context, CancellationToken ct) 
{
    if(somethingWrong)
        return Diagnostics.Error("code42", "Something went wrong") //will create a static instance
            .CreateDiagnosticInfo(new (context.TargetSymbol)); //instance info
}
```

Since most diagnostics can be specific for only the part of the generation where it is discovered, this
keeps the diagnostic information inside that location in the code, without losing the need and benefit
of a static DiagnosticDescriptor.

---

## Builder for complex transforms

When a transform has multiple validation steps and potentially multiple diagnostics, a builder is available

```csharp
static TransformResult<MyCreationInfo> Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken token)
{
    var builder = Transform.Build<MyCreationInfo>();

    if (!IsValid(ctx))
        builder.Diagnostics.Add(diagnostic);

    TransformResult<AnotherTransform> otherTransform = SomeOtherTransFormation();
    builder.Diagnostics.AddRange(otherTransform.Diagnostics); //combine diagnostics

    if (IsFatal(ctx))
        return builder.FatalError(errorDiagnostic);

    builder.Result = BuildCreationInfo(ctx);

    return builder;
}
```

The implicit conversion from `TransformResultBuilder<T>` to `TransformResult<T>` means `return builder` works,
but you can also use the explicit `builder.GetResult()`

---


## Pipeline helpers

There are some other extension methods on `IncrementalValuesProvider<TransformResult<T>>` that can be
used in case you do not want to use the 'fast track'
For example:

```csharp
var transformResults = context.SyntaxProvider.ForAttributeWithMetadataName(...,predicate,  Transform); //using the default functionality, not the library.

// Register diagnostics separately, get results back
var results = transformResults.RegisterDiagnosticsAndReturnResults(context);

// Or handle each concern independently
transformResults.RegisterDiagnosticsOutput(context);
var results = transformResults.GetResults();

// Filter to non-empty results only (containing either a Result, diagnostics or both)
var nonEmpty = transformResults.WhereNotEmpty();
```

---

## Return multiple Transform results

In 1.4, returning multiple results was added to make returning multiple results easier.
Of course you can always return a `TransformResult<ImmutableArray<YourMetaObjectType>>`, but then at least
you should use `TransformResult<EquatableArray<YourMetaObjectType>>`

However, starting in 1.4, you can return the transformresults as a collection or even enumerable directly, and the proper overload of
`CreateValuesProvider` or `ValuesForAttributeWithMetadataName` will be used.
That means that you can always change the transform result to a collection later. RegisterSourceOutput will still receive
a `IncrementalValuesProvider<T>` and you do not need to change the code generation part.

The main advantage is when you don't necessarily expect multiple results, but it can happen.
For example, multiple attributes on the same object, if that is allowed. You can read the attributes
and handle every entry one by one, without needing to accumulate results into a collection first.

Reusing the exact same call as in the [Fast track](#fast-track-examples) example:
```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
    var results = context.ValuesForAttributeWithMetadataName<MyCreationInfo>(
        "MyAttribute", Predicate, TransformFunction); 

    context.RegisterSourceOutput(results, YourCodeGeneration);  
}

// results will only contain the valid results; if any diagnostics have been returned, they
// will have been handled by the library
```

You can change the transform to e.g.

```csharp

static ImmutableArray<TransformResult<YourMetaDataObject>> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{

}
```

Note that this does *not* need to be an equatable array, because the created IncrementalValuesProvider for both the results and diagnostics
do not use the inbetween result. At least not when using the extension funcion in the libraries. This would only be an intermediate provider.
The endpoints use either the Result type (which should be equatable) or, in the case of diagnostics, implicitly handled by the library.
So as long as RegisterSourceOutput is only used at the end result you obtain, you don't have to worry about the returning type.

Therefore, you can even just use

```csharp
static IEnumerable<TransformResult<YourMetaDataObject>> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{

}
```

And, regardless if it is a good idea or not, you can even do something like:

```csharp
static IEnumerable<TransformResult<YourMetaDataObject>> TransformFunction(GeneratorSyntaxContext context, CancellationToken ct) 
{
    foreach(var symbol in aSymbolCollection)
    {
        if(someReason)
            yield return SomeDiagnostic; //a DiagnosticsInfo
        else if(aCertainType)
            yield return ASpecificResult; //a result of type YourMetaDataObject
        else
            yield return AnotherResult; //a result of type YourMetaDataObject
    }
}
```

---

## Installation

```
dotnet add package Subro.Generators.TransformResult
```

This is a source package — the types are compiled directly into your generator project, with no runtime dependency for consumers of your generator.