﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Mapster.Models;
using Mapster.Utils;

namespace Mapster.Adapters
{
    public abstract class BaseAdapter
    {
        public abstract int? Priority(Type sourceType, Type destinationType, MapType mapType);

        public LambdaExpression CreateAdaptFunc(CompileArgument arg)
        {
            var p = Expression.Parameter(arg.SourceType);
            var body = CreateExpressionBody(p, null, arg);
            return Expression.Lambda(body, p);
        }

        public LambdaExpression CreateAdaptToTargetFunc(CompileArgument arg)
        {
            var p = Expression.Parameter(arg.SourceType);
            var p2 = Expression.Parameter(arg.DestinationType);
            var body = CreateExpressionBody(p, p2, arg);
            return Expression.Lambda(body, p, p2);
        }

        public TypeAdapterRule CreateRule()
        {
            var rule = new TypeAdapterRule
            {
                Priority = this.Priority,
                Settings = new TypeAdapterSettings
                {
                    ConverterFactory = this.CreateAdaptFunc,
                    ConverterToTargetFactory = this.CreateAdaptToTargetFunc,
                }
            };
            DecorateRule(rule);
            return rule;
        }

        protected virtual void DecorateRule(TypeAdapterRule rule) { }

        protected virtual bool CanInline(Expression source, Expression destination, CompileArgument arg)
        {
            if (arg.MapType == MapType.MapToTarget)
                return false;
            if (arg.Settings.ConstructUsing != null &&
                arg.Settings.ConstructUsing.Body.NodeType != ExpressionType.New &&
                arg.Settings.ConstructUsing.Body.NodeType != ExpressionType.MemberInit)
            {
                if (arg.MapType == MapType.Projection)
                    throw new InvalidOperationException(
                        $"Input ConstructUsing is invalid for projection: TSource: {arg.SourceType} TDestination: {arg.DestinationType}");
                return false;
            }
            if (arg.Settings.PreserveReference == true &&
                arg.MapType != MapType.Projection &&
                !arg.SourceType.GetTypeInfo().IsValueType &&
                !arg.DestinationType.GetTypeInfo().IsValueType)
                return false;
            return true;
        }

        protected virtual Expression CreateExpressionBody(Expression source, Expression destination, CompileArgument arg)
        {
            return CanInline(source, destination, arg) 
                ? CreateInlineExpressionBody(source, arg).To(arg.DestinationType) 
                : CreateBlockExpressionBody(source, destination, arg);
        }

        protected Expression CreateBlockExpressionBody(Expression source, Expression destination, CompileArgument arg)
        {
            var result = Expression.Variable(arg.DestinationType);
            Expression assign = Expression.Assign(result, destination ?? CreateInstantiationExpression(source, arg));

            var set = CreateBlockExpression(source, result, arg);

            if (arg.Settings.PreserveReference == true &&
                arg.MapType != MapType.Projection &&
                !arg.SourceType.GetTypeInfo().IsValueType &&
                !arg.DestinationType.GetTypeInfo().IsValueType)
            {
                //using (var scope = new MapContextScope()) {
                //  var dict = scope.Context.Reference;
                //  object cache;
                //  if (dict.TryGetValue(source, out cache))
                //      result = (TDestination)cache;
                //  else {
                //      result = new TDestination();
                //      dict.Add(source, (object)result);
                //      result.Prop1 = adapt(source.Prop1);
                //      result.Prop2 = adapt(source.Prop2);
                //  }
                //}

                var scope = Expression.Variable(typeof(MapContextScope));
                var newScope = Expression.Assign(scope, Expression.New(typeof(MapContextScope)));

                var dict = Expression.Variable(typeof (Dictionary<object, object>));
                var refContext = Expression.Property(scope, "Context");
                var refDict = Expression.Property(refContext, "References");
                var assignDict = Expression.Assign(dict, refDict);

                var refAdd = Expression.Call(dict, "Add", null, Expression.Convert(source, typeof(object)), Expression.Convert(result, typeof(object)));
                var setResultAndCache = Expression.Block(assign, refAdd, set);

                var cached = Expression.Variable(typeof(object));
                var tryGetMethod = typeof(Dictionary<object, object>).GetMethod("TryGetValue", new[] { typeof(object), typeof(object).MakeByRefType() });
                var checkHasRef = Expression.Call(dict, tryGetMethod, source, cached);
                var setResult = Expression.IfThenElse(
                    checkHasRef,
                    ExpressionEx.Assign(result, cached),
                    setResultAndCache);
                var usingBody = Expression.Block(new[] { cached, dict }, assignDict, setResult);

                var dispose = Expression.Call(scope, "Dispose", null);
                set = Expression.Block(new[] { scope }, newScope, Expression.TryFinally(usingBody, dispose));
            }
            else
            {
                set = Expression.Block(assign, set);
            }

            //TDestination result;
            //if (source == null)
            //  result = default(TDestination);
            //else
            //  result = adapt(source);
            //return result;
            if (arg.MapType != MapType.Projection && 
                (!arg.SourceType.GetTypeInfo().IsValueType || arg.SourceType.IsNullable()))
            {
                var compareNull = Expression.Equal(source, Expression.Constant(null, source.Type));
                set = Expression.IfThenElse(
                    compareNull,
                    Expression.Assign(result, destination ?? Expression.Constant(arg.DestinationType.GetDefault(), arg.DestinationType)),
                    set);
            }

            return Expression.Block(new[] { result }, set, result);
        }
        protected Expression CreateInlineExpressionBody(Expression source, CompileArgument arg)
        {
            //source == null ? default(TDestination) : convert(source)

            var exp = CreateInlineExpression(source, arg);

            if (arg.MapType != MapType.Projection
                && (!arg.SourceType.GetTypeInfo().IsValueType || arg.SourceType.IsNullable()))
            {
                var compareNull = Expression.Equal(source, Expression.Constant(null, source.Type));
                exp = Expression.Condition(
                    compareNull,
                    Expression.Constant(exp.Type.GetDefault(), exp.Type),
                    exp);
            }

            return exp;
        }

        protected abstract Expression CreateBlockExpression(Expression source, Expression destination, CompileArgument arg);
        protected abstract Expression CreateInlineExpression(Expression source, CompileArgument arg);

        protected virtual Expression CreateInstantiationExpression(Expression source, CompileArgument arg)
        {
            //new TDestination()

            return arg.Settings.ConstructUsing != null 
                ? arg.Settings.ConstructUsing.Apply(source).TrimConversion().To(arg.DestinationType) 
                : Expression.New(arg.DestinationType);
        }

        protected Expression CreateAdaptExpression(Expression source, Type destinationType, CompileArgument arg)
        {
            if (source.Type == destinationType && (arg.Settings.ShallowCopyForSameType == true || arg.MapType == MapType.Projection))
                return source.To(destinationType);

            var lambda = arg.Context.Config.CreateInlineMapExpression(source.Type, destinationType, arg.MapType, arg.Context);
            var exp = lambda.Apply(source);

            if (arg.Settings.DestinationTransforms.Transforms.ContainsKey(exp.Type))
            {
                var transform = arg.Settings.DestinationTransforms.Transforms[exp.Type];
                var replacer = new ParameterExpressionReplacer(transform.Parameters, exp);
                var newExp = replacer.Visit(transform.Body);
                exp = replacer.ReplaceCount >= 2 ? Expression.Invoke(transform, exp) : newExp;
            }
            return exp.To(destinationType);
        }
    }
}
