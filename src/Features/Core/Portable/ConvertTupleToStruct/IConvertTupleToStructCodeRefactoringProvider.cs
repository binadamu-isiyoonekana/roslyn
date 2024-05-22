﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.ConvertTupleToStruct;

internal interface IConvertTupleToStructCodeRefactoringProvider : ILanguageService
{
    Task<Solution> ConvertToStructAsync(
        Document document, TextSpan span, Scope scope, CleanCodeGenerationOptionsProvider fallbackOptions, bool isRecord, CancellationToken cancellationToken);
}
