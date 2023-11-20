using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Core;
using Marten.Exceptions;
using Marten.Linq.Fields;
using NpgsqlTypes;
using Weasel.Postgresql.SqlGeneration;

namespace Marten.Linq.Parsing.Methods;

internal abstract class StringComparisonParser: IMethodCallParser
{
    private readonly MethodInfo[] _supportedMethods;

    public StringComparisonParser(params MethodInfo[] supportedMethods)
    {
        _supportedMethods = supportedMethods;
    }

    public bool Matches(MethodCallExpression expression)
    {
        return _supportedMethods.Any(m => AreMethodsEqual(m, expression.Method));
    }

    public ISqlFragment Parse(IFieldMapping mapping, IReadOnlyStoreOptions options, MethodCallExpression expression)
    {
        var locator = GetLocator(mapping, expression);

        ConstantExpression value;
        if (expression.Object?.NodeType == ExpressionType.Constant)
        {
            value = (ConstantExpression)expression.Object;
        }
        else
        {
            value = expression.Arguments.OfType<ConstantExpression>().FirstOrDefault();
        }

        if (value == null)
        {
            throw new BadLinqExpressionException("Could not extract string value from {0}.".ToFormat(expression), null);
        }

        var comparisonType = GetComparisonType(expression);

        var stringOperator = comparisonType switch
        {
            ComparisonType.Exact => "=",
            ComparisonType.Like => "LIKE",
            ComparisonType.ILike => "ILIKE",
            _ => throw new ArgumentOutOfRangeException()
        };

        locator = comparisonType switch
        {
            ComparisonType.Exact => $"lower({locator})",
            _ => locator
        };

        var paramReplacementToken = comparisonType switch
        {
            ComparisonType.Exact => $"lower(?)",
            _ => "?"
        };

        var parameterValue = FormatValue(expression.Method, value.Value as string);
        var param = parameterValue == null
            ? new CommandParameter(DBNull.Value, NpgsqlDbType.Varchar)
            : new CommandParameter(parameterValue, NpgsqlDbType.Varchar);

        // Do not use escape char when using case insensitivity
        // this way backslash does not have special meaning and works as string literal
        var escapeChar = string.Empty;
        if (comparisonType == ComparisonType.ILike)
        {
            escapeChar = " ESCAPE ''";
        }

        return new CustomizableWhereFragment($"{locator} {stringOperator} {paramReplacementToken}{escapeChar}", "?", param);
    }

    protected bool AreMethodsEqual(MethodInfo method1, MethodInfo method2)
    {
        return method1.DeclaringType == method2.DeclaringType && method1.Name == method2.Name
                                                              && method1.GetParameters().Select(p => p.ParameterType)
                                                                  .SequenceEqual(method2.GetParameters()
                                                                      .Select(p => p.ParameterType));
    }

    /// <summary>
    ///     Formats the string value as appropriate for the comparison.
    /// </summary>
    /// <param name="method"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public abstract string FormatValue(MethodInfo method, string value);

    protected virtual bool IsCaseInsensitiveComparison(MethodCallExpression expression)
    {
        var comparison = expression.Arguments.OfType<ConstantExpression>()
            .Where(a => a.Type == typeof(StringComparison)).Select(c => (StringComparison)c.Value).FirstOrDefault();

        var ignoreCaseComparisons = new[]
        {
            StringComparison.CurrentCultureIgnoreCase, StringComparison.InvariantCultureIgnoreCase,
            StringComparison.OrdinalIgnoreCase
        };
        if (ignoreCaseComparisons.Contains(comparison))
        {
            return true;
        }

        return false;
    }

    protected enum ComparisonType
    {
        Exact,
        ILike,
        Like
    }

    protected virtual ComparisonType GetComparisonType(MethodCallExpression expression)
    {
        var comparison = expression.Arguments.OfType<ConstantExpression>()
            .Where(a => a.Type == typeof(StringComparison))
            .Select(c => (StringComparison)c.Value)
            .FirstOrDefault();

        return comparison switch
        {
            StringComparison.CurrentCultureIgnoreCase or StringComparison.InvariantCultureIgnoreCase => ComparisonType.ILike,
            StringComparison.OrdinalIgnoreCase => ComparisonType.Exact,
            _ => ComparisonType.Like
        };
    }

    /// <summary>
    ///     Returns the operator to emit (e.g. LIKE/ILIKE).
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    protected virtual string GetOperator(MethodCallExpression expression)
    {
        return IsCaseInsensitiveComparison(expression) ? "ILIKE" : "LIKE";
    }

    /// <summary>
    ///     Returns a locator for the member being queried upon
    /// </summary>
    /// <param name="mapping"></param>
    /// <param name="expression"></param>
    /// <returns></returns>
    protected string GetLocator(IFieldMapping mapping, MethodCallExpression expression)
    {
        var memberExpression = determineStringField(expression);
        return mapping.FieldFor(memberExpression).RawLocator;
    }

    private static Expression determineStringField(MethodCallExpression expression)
    {
        if (!expression.Method.IsStatic && expression.Object != null &&
            expression.Object.NodeType != ExpressionType.Constant)
        {
            // x.member.Equals(...)
            return expression.Object;
        }

        if (expression.Arguments[0].NodeType == ExpressionType.Constant)
        {
            // string.Equals("value", x.member)
            return expression.Arguments[1];
        }

        // string.Equals(x.member, "value")
        return expression.Arguments[0];
    }
}
