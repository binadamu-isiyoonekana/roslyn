﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.TaskList;

namespace Microsoft.CodeAnalysis.TodoComments
{
    internal readonly struct DocumentAndComments
    {
        public readonly DocumentId DocumentId;
        public readonly ImmutableArray<TaskListItem> Comments;

        public DocumentAndComments(DocumentId documentId, ImmutableArray<TaskListItem> comments)
        {
            DocumentId = documentId;
            Comments = comments;
        }
    }
}
