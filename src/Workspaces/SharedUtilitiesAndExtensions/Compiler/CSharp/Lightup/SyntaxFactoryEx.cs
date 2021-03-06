﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if false // Not currently used, but preserved so the pattern is easy to adopt when needed
using Microsoft.CodeAnalysis.CSharp.Syntax;

#if CODE_STYLE
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
#endif
#endif

namespace Microsoft.CodeAnalysis.CSharp.Shared.Lightup
{
    internal static class SyntaxFactoryEx
    {
#if false // Not currently used, but preserved so the pattern is easy to adopt when needed
#if CODE_STYLE
        private static readonly Func<TypeSyntax, PatternSyntax> TypePatternAccessor;

        static SyntaxFactoryEx()
        {
            var typePatternMethods = typeof(SyntaxFactory).GetTypeInfo().GetDeclaredMethods(nameof(TypePattern));
            var typePatternMethod = typePatternMethods.FirstOrDefault(method => method.GetParameters().Length == 1);
            if (typePatternMethod is object)
            {
                var typeParameter = Expression.Parameter(typeof(TypeSyntax), "type");
                var expression = Expression.Lambda<Func<TypeSyntax, PatternSyntax>>(
                    Expression.Call(
                        typePatternMethod,
                        typeParameter),
                    typeParameter);
                TypePatternAccessor = expression.Compile();
            }
            else
            {
                TypePatternAccessor = ThrowNotSupportedOnFallback<TypeSyntax, PatternSyntax>(nameof(SyntaxFactory), nameof(TypePattern));
            }
        }

        public static PatternSyntax TypePattern(TypeSyntax type)
        {
            return TypePatternAccessor(type);
        }

        private static Func<T, TResult> ThrowNotSupportedOnFallback<T, TResult>(string typeName, string methodName)
        {
            return _ => throw new NotSupportedException(string.Format(CSharpCompilerExtensionsResources._0_1_is_not_supported_in_this_version, typeName, methodName));
        }
#else
        public static PatternSyntax TypePattern(TypeSyntax type) => SyntaxFactory.TypePattern(type);
#endif
#endif
    }
}

