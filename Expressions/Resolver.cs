﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Expressions.Ast;
using Expressions.Expressions;
using MethodCall = Expressions.Ast.MethodCall;

namespace Expressions
{
    internal class Resolver
    {
        private readonly DynamicExpression _dynamicExpression;
        private readonly Type _ownerType;
        private readonly Import[] _imports;
        private readonly Type[] _identifierTypes;
        private readonly bool _ignoreCase;
        private readonly int[] _identifierIndexes;

        public Resolver(DynamicExpression dynamicExpression, Type ownerType, Import[] imports, Type[] identifierTypes, int[] parameterMap)
        {
            Require.NotNull(dynamicExpression, "dynamicExpression");
            Require.NotNull(imports, "imports");
            Require.NotNull(identifierTypes, "identifierTypes");
            Require.NotNull(parameterMap, "parameterMap");

            _dynamicExpression = dynamicExpression;
            _ownerType = ownerType;
            _imports = imports;
            _identifierTypes = identifierTypes;

            // Inverse the parameter map.

            _identifierIndexes = new int[_identifierTypes.Length];

            for (int i = 0; i < _identifierTypes.Length; i++)
            {
                _identifierIndexes[i] = -1;

                if (_identifierTypes[i] != null)
                {
                    for (int j = 0; j < parameterMap.Length; j++)
                    {
                        if (parameterMap[j] == i)
                        {
                            _identifierIndexes[i] = j;
                            break;
                        }
                    }

                    Debug.Assert(_identifierIndexes[i] != -1);
                }
            }

            _ignoreCase = !DynamicExpression.IsLanguageCaseSensitive(_dynamicExpression.Language);
        }

        public IExpression Resolve(IAstNode node)
        {
            return node.Accept(new Visitor(this));
        }

        private Type ResolveType(string type, int arrayIndex)
        {
            string builtInType = type;

            if (_ignoreCase)
                builtInType = builtInType.ToLowerInvariant();

            var result = TypeUtil.GetBuiltInType(builtInType);

            if (result == null)
            {
                result = Type.GetType(type, false);

                if (result == null)
                    throw new NotSupportedException(String.Format("Unknown type '{0}'", type));
            }

            if (arrayIndex == 1)
                result = result.MakeArrayType();
            else if (arrayIndex > 1)
                result = result.MakeArrayType(arrayIndex);

            return result;
        }

        private Type ResolveExpressionType(Type left, Type right, ExpressionType type)
        {
            Require.NotNull(left, "left");
            Require.NotNull(right, "right");

            if (left == right)
                return left;

            // TODO: Implicit/explicit operators and operators for the expression type.

            // Special cast for adding strings.

            if (type == ExpressionType.Add && (left == typeof(string) || right == typeof(string)))
                return typeof(string);

            // Can we cast implicitly?

            var leftTable = TypeUtil.GetImplicitCastingTable(left);
            var rightTable = TypeUtil.GetImplicitCastingTable(right);

            if (leftTable != null && rightTable != null)
            {
                for (int i = 0; i < leftTable.Count; i++)
                {
                    if (leftTable[i] == right)
                        return leftTable[i];

                    for (int j = 0; j < rightTable.Count; j++)
                    {
                        if (leftTable[i] == rightTable[j])
                            return leftTable[i];
                    }
                }
            }

            // We can't cast implicitly.

            throw new NotSupportedException(String.Format("Cannot implicitly cast {0} and {1}", left, right));
        }

        private MethodInfo ResolveMethodGroup(IList<MethodInfo> methods, IExpression[] arguments)
        {
            var argumentTypes = new Type[arguments == null ? 0 : arguments.Length];

            for (int i = 0; i < argumentTypes.Length; i++)
            {
                argumentTypes[i] = arguments[i].Type;
            }

            // Get all methods with the correct number of parameters.

            var candidates = GetCandidates(methods, argumentTypes.Length);

            if (candidates.Count == 0)
                throw new NotSupportedException(String.Format("Cannot resolve method {0}", methods[0].Name));

            if (candidates.Count == 1)
                return candidates[0];

            // See if we have an exact match.

            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == argumentTypes[i])
                        return candidate;
                }
            }

            // See if we can find a match with implicit casting.

            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                bool success = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    var castingTable = TypeUtil.GetImplicitCastingTable(argumentTypes[i]);

                    if (castingTable != null)
                    {
                        bool found = false;

                        for (int j = 0; j < castingTable.Count; j++)
                        {
                            if (castingTable[j] == parameters[i].ParameterType)
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            success = false;
                            break;
                        }
                    }
                    else
                    {
                        if (!parameters[i].ParameterType.IsAssignableFrom(argumentTypes[i]))
                        {
                            success = false;
                            break;
                        }
                    }
                }

                if (success)
                    return candidate;
            }

            throw new NotSupportedException(String.Format("Cannot resolve method {0}", methods[0].Name));
        }

        private List<MethodInfo> GetCandidates(IEnumerable<MethodInfo> methods, int arguments)
        {
            var candidates = new List<MethodInfo>();

            foreach (var method in methods)
            {
                if (method.GetParameters().Length == arguments)
                    candidates.Add(method);
            }

            return candidates;
        }

        private class Visitor : IAstVisitor<IExpression>
        {
            private readonly Resolver _resolver;

            public Visitor(Resolver resolver)
            {
                _resolver = resolver;
            }

            public IExpression BinaryExpression(Ast.BinaryExpression binaryExpression)
            {
                var left = binaryExpression.Left.Accept(this);
                var right = binaryExpression.Right.Accept(this);
                var type = _resolver.ResolveExpressionType(left.Type, right.Type, binaryExpression.Type);

                return new Expressions.BinaryExpression(left, right, binaryExpression.Type, type);
            }

            public IExpression Cast(Ast.Cast cast)
            {
                return new Expressions.Cast(
                    cast.Operand.Accept(this),
                    _resolver.ResolveType(cast.Type.Name, cast.Type.ArrayIndex)
                );
            }

            public IExpression Constant(Ast.Constant constant)
            {
                return new Expressions.Constant(constant.Value);
            }

            public IExpression IdentifierAccess(IdentifierAccess identifierAccess)
            {
                // First try variables.

                var identifiers = _resolver._dynamicExpression.ParseResult.Identifiers;

                for (int i = 0; i < _resolver._identifierTypes.Length; i++)
                {
                    if (
                        _resolver._identifierTypes[i] != null &&
                        IdentifiersEqual(identifiers[i].Name, identifierAccess.Name)
                    )
                        return new VariableAccess(_resolver._identifierTypes[i], _resolver._identifierIndexes[i]);
                }

                // Next, we go through the owner type.

                if (_resolver._ownerType != null)
                {
                    var result = Resolve(new VariableAccess(_resolver._ownerType, 0), identifierAccess.Name);

                    if (result == null)
                        result = Resolve(new TypeAccess(_resolver._ownerType), identifierAccess.Name);

                    if (result != null)
                        return result;
                }

                // Next, imports. Namespaces have precedence.

                foreach (var import in _resolver._imports)
                {
                    if (import.Namespace != null && IdentifiersEqual(import.Namespace, identifierAccess.Name))
                        return new TypeAccess(import.Type);
                }

                // And last, members of the imports.

                foreach (var import in _resolver._imports)
                {
                    if (import.Namespace == null)
                    {
                        var result = Resolve(new TypeAccess(import.Type), identifierAccess.Name);

                        if (result != null)
                            return result;
                    }
                }

                throw new NotSupportedException("Could not resolve identifier");
            }

            private IExpression Resolve(IExpression operand, string member)
            {
                bool isStatic = operand is TypeAccess;

                // First try properties and methods.

                var methods = operand.Type.GetMethods(
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public
                );

                var propertyMethods = new List<MethodInfo>();
                string propertyMember = "get_" + member;

                foreach (var method in methods)
                {
                    // We do not check for IsSpecialName. NHibernate is a known
                    // bug where generated properties do not have IsSpecialName set,
                    // and it shouldn't be an issue.

                    if (IdentifiersEqual(method.Name, propertyMember))
                        propertyMethods.Add(method);
                }

                if (propertyMethods.Count == 1)
                    return new Expressions.MethodCall(operand, propertyMethods[0], null);
                else if (propertyMethods.Count > 1)
                    return null;

                // Next, try for a field.

                var field = operand.Type.GetField(
                    member,
                    (_resolver._ignoreCase ? BindingFlags.IgnoreCase : 0) |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance) |
                    BindingFlags.Public
                );

                if (field != null)
                    return new FieldAccess(operand, field);

                // Nothing matched.

                return null;
            }

            private bool IdentifiersEqual(string a, string b)
            {
                return String.Equals(
                    a, b, _resolver._ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                );
            }

            public IExpression Index(Ast.Index index)
            {
                var arguments = ResolveArguments(index.Arguments);

                var operand = index.Operand.Accept(this);

                if (operand.Type.IsArray)
                {
                    if (arguments.Length != operand.Type.GetArrayRank())
                        throw new NotSupportedException("Rank of array is incorrect");

                    foreach (var argument in arguments)
                    {
                        if (!TypeUtil.IsCastAllowed(argument.Type, typeof(int)))
                            throw new NotSupportedException("Arguments of array index must be convertable to int");
                    }

                    return new Expressions.Index(operand, arguments, operand.Type.GetElementType());
                }

                var defaultMemberAttributes = operand.Type.GetCustomAttributes(typeof(DefaultMemberAttribute), true);

                if (defaultMemberAttributes.Length != 1)
                    throw new NotSupportedException("Operand does not support indexing");

                var result = ResolveMethod(operand, "get_" + ((DefaultMemberAttribute)defaultMemberAttributes[0]).MemberName, arguments);

                if (result == null)
                    throw new NotSupportedException("Cannot resolve index method");

                return result;
            }

            public IExpression MemberAccess(MemberAccess memberAccess)
            {
                throw new NotImplementedException();
            }

            public IExpression MethodCall(Ast.MethodCall methodCall)
            {
                var arguments = ResolveArguments(methodCall.Arguments);

                var identifierAccess = methodCall.Operand as IdentifierAccess;

                if (identifierAccess != null)
                    return ResolveGlobalMethod(identifierAccess.Name, arguments);

                var memberAccess = methodCall.Operand as MemberAccess;

                if (memberAccess != null)
                {
                    var result = ResolveMethod(memberAccess.Operand.Accept(this), memberAccess.Member, arguments);

                    if (result != null)
                        return result;
                }

                throw new NotSupportedException("Cannot resolve method call on " + methodCall.Operand.GetType().Name);
            }

            private IExpression[] ResolveArguments(AstNodeCollection astArguments)
            {
                var arguments = new IExpression[astArguments == null ? 0 : astArguments.Nodes.Count];

                for (int i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = astArguments.Nodes[i].Accept(this);
                }
                return arguments;
            }

            private Expressions.MethodCall ResolveGlobalMethod(string name, IExpression[] arguments)
            {
                // Try methods on the owner type.

                if (_resolver._ownerType != null)
                {
                    var result = ResolveMethod(new TypeAccess(_resolver._ownerType), name, arguments);

                    if (result == null)
                        result = ResolveMethod(new VariableAccess(_resolver._ownerType, 0), name, arguments);

                    if (result != null)
                        return result;
                }

                // Try static methods on the imports.

                foreach (var import in _resolver._imports)
                {
                    var result = ResolveMethod(new TypeAccess(import.Type), name, arguments);

                    if (result != null)
                        return result;
                }

                throw new NotSupportedException("Could not resolve method");
            }

            private Expressions.MethodCall ResolveMethod(IExpression operand, string name, IExpression[] arguments)
            {
                bool isStatic = operand is TypeAccess;

                var methods = operand.Type.GetMethods(
                    BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                );

                var candidates = new List<MethodInfo>();

                foreach (var method in methods)
                {
                    if (IdentifiersEqual(method.Name, name))
                        candidates.Add(method);
                }

                if (candidates.Count > 0)
                {
                    var method = _resolver.ResolveMethodGroup(candidates, arguments);

                    return new Expressions.MethodCall(operand, method, arguments);
                }

                return null;
            }

            public IExpression UnaryExpression(Ast.UnaryExpression unaryExpression)
            {
                var operand = unaryExpression.Operand.Accept(this);
                Type type;

                switch (unaryExpression.Type)
                {
                    case ExpressionType.Plus:
                        if (!TypeUtil.IsNumeric(operand.Type))
                            throw new NotSupportedException("Cannot plus non numeric type");

                        type = operand.Type;
                        break;

                    case ExpressionType.Minus:
                        if (!TypeUtil.IsNumeric(operand.Type))
                            throw new NotSupportedException("Cannot plus non numeric type");

                        // TODO: Make constants signed and handle minus on unsigned's.

                        type = operand.Type;
                        break;

                    case ExpressionType.Not:
                        if (operand.Type != typeof(bool))
                            throw new NotSupportedException("Cannot not non boolean types");

                        type = typeof(bool);
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                return new Expressions.UnaryExpression(operand, type, unaryExpression.Type);
            }
        }
    }
}
