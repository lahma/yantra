﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using YantraJS.Core.CodeGen;
using YantraJS.ExpHelper;
using YantraJS.Expressions;
using Exp = YantraJS.Expressions.YExpression;
using Expression = YantraJS.Expressions.YExpression;
using ParameterExpression = YantraJS.Expressions.YParameterExpression;
using LambdaExpression = YantraJS.Expressions.YLambdaExpression;
using LabelTarget = YantraJS.Expressions.YLabelTarget;
using SwitchCase = YantraJS.Expressions.YSwitchCaseExpression;
using GotoExpression = YantraJS.Expressions.YGoToExpression;
using TryExpression = YantraJS.Expressions.YTryCatchFinallyExpression;

namespace YantraJS.Core.LinqExpressions.GeneratorsV2
{


    public class GeneratorRewriter: YExpressionMapVisitor
    {

        private ParameterExpression pe;
        private readonly ParameterExpression args;
        private readonly ParameterExpression nextJump;
        private readonly ParameterExpression nextValue;
        private readonly ParameterExpression exception;
        private readonly YFieldExpression StackItem;
        private readonly YFieldExpression Context;
        // private readonly YFieldExpression ScriptInfo;
        // private readonly YFieldExpression Closures;
        private LabelTarget generatorReturn;
        private readonly Sequence<(ParameterExpression original, ParameterExpression box, int index)> lifted;
        private LabelTarget @return;
        private readonly ParameterExpression replaceArgs;
        // private readonly ParameterExpression replaceStackItem;
        private readonly ParameterExpression replaceContext;
        private readonly ParameterExpression replaceScriptInfo;
        private Sequence<(LabelTarget label, int id)> jumps = new Sequence<(LabelTarget label, int id)>();

        public GeneratorRewriter(
            ParameterExpression pe, 
            LabelTarget @return, 
            ParameterExpression replaceArguments,
            // ParameterExpression replaceStackItem,
            ParameterExpression replaceContext
            // ParameterExpression replaceScriptInfo
            )
        {
            this.pe = pe;
            this.args = Expression.Parameter(typeof(Arguments).MakeByRefType(), "args");
            this.nextJump = Expression.Parameter(typeof(int), "nextJump");
            this.nextValue = Expression.Parameter(typeof(JSValue), "nextValue");
            this.exception = Expression.Parameter(typeof(Exception), "ex");
            // this.StackItem = Expression.Field(pe, "StackItem");
            this.Context = Expression.Field(pe, "Context");
            // this.ScriptInfo = Expression.Field(pe, "ScriptInfo");
            // this.Closures = Expression.Field(pe, "Closures");
            this.replaceArgs = replaceArguments;
            // this.replaceStackItem = replaceStackItem;
            this.replaceContext = replaceContext;
            // this.replaceScriptInfo = replaceScriptInfo;
            this.@return = @return;
            this.generatorReturn = Expression.Label(typeof(GeneratorState), "RETURN");
            this.lifted = new Sequence<(ParameterExpression original, ParameterExpression box, int index)>();
        }

        public static LambdaExpression Rewrite(
            in FunctionName name,
           Expression body,
           LabelTarget r,
           ParameterExpression generator,
           ParameterExpression replaceArgs,
           ParameterExpression replaceStackItem,
           ParameterExpression replaceContext,
           ParameterExpression replaceScriptInfo)
        {
            var gw = new GeneratorRewriter(generator, r, replaceArgs /*,replaceStackItem,*/, replaceContext /*,replaceScriptInfo*/);

            body = MethodRewriter.Rewrite(body);

            var flatten = new FlattenBlocks();
            var innerBody = flatten.Visit( gw.Visit(body));

            // setup jump table...

            var @break = YExpression.Label("generatorEnd");

            var jumpExp = gw.GenerateJumps(@break);

            var (boxes, inits) = gw.LoadBoxes();

            YBlockExpression newBody;

            if (boxes == null)
            {
                newBody = Expression.Block(jumpExp,
                                innerBody,
                                Expression.Label(gw.generatorReturn, GeneratorStateBuilder.New(0))
                                );
            }
            else
            {

                newBody = Expression.Block(
                    boxes,

                    // load boxes...
                    inits,

                    jumpExp,
                    YExpression.Label(@break),
                    innerBody,
                    Expression.Label(gw.generatorReturn, GeneratorStateBuilder.New(0))
                    );
            }

            return Expression.Lambda<JSGeneratorDelegateV2>(
                in name, 
                newBody, generator, gw.args, gw.nextJump, gw.nextValue, gw.exception);
        }

        private (Sequence<ParameterExpression> boxes, Expression init) LoadBoxes()
        {
            var boxes = new Sequence<Expression>(lifted.Count);
            boxes.Add(ClrGeneratorV2Builder.InitVariables(pe, lifted.Count));
            var vlist = new Sequence<ParameterExpression>(lifted.Count);
            foreach(var v in lifted)
            {
                vlist.Add(v.box);
                boxes.Add(Expression.Assign( v.box, ClrGeneratorV2Builder.GetVariable(pe, v.index, v.original.Type )));
            }

            if(vlist.Count == 0)
            {
                return (null, null);
            }

            return (vlist, Expression.Block(boxes));
        }

        private Expression GenerateJumps(YLabelTarget @break)
        {
            if (jumps.Count == 0)
                return Expression.Empty;
            var cases = new Sequence<YLabelTarget>();
            var offset = 1;            
            jumps = new  Sequence<(LabelTarget label, int id)>(jumps.OrderBy(x => x.id));
            var en = jumps.GetFastEnumerator();
            while(en.MoveNext(out var jump, out var i))
            {
                var (label, id) = jump;
                var index = id + offset;
                // this will fill the gap in between jumps, if any
                while(index > cases.Count)
                {
                    cases.Add(@break);
                }
                cases.Add(label);
            }
            return Expression.JumpSwitch( nextJump + offset, cases);
        }

        protected override Expression VisitBlock(YBlockExpression node)
        {
            if (!node.HasYield())
                return base.VisitBlock(node);
            var list = new Sequence<Expression>(node.Variables.Count + node.Expressions.Count);
            var ve = node.Variables.GetFastEnumerator();
            while(ve.MoveNext(out var v))
            {
                int index = lifted.Count;
                var box = Expression.Parameter(typeof(Box<>).MakeGenericType(v.Type));
                lifted.Add((v, box, index));
            }
            var vne = node.Expressions.GetFastEnumerator();
            while(vne.MoveNext(out var s))
            {
                list.Add(Visit(s));
            }
            return Expression.Block(
                list
                );
        }
        protected override Exp VisitReturn(YReturnExpression node)
        {
            if(node.Default != null)
            {
                if(node.Default.NodeType == YExpressionType.Yield)
                {
                    // return yield case... need to expand..
                    var yield = node.Default as YYieldExpression;
                    var arg = Visit(yield.Argument);
                    var (label, id) = GetNextYieldJumpTarget();
                    return Expression.Block(
                        Expression.Return(generatorReturn,
                            GeneratorStateBuilder.New(arg, id)),
                        Expression.Label(label),
                        Expression.Return(
                            generatorReturn,
                            GeneratorStateBuilder.New(nextValue, -1))
                        );

                }
            }

            return Expression.Return(generatorReturn,
                GeneratorStateBuilder.New(Visit(node.Default), -1));
        }

        protected override Expression VisitGoto(GotoExpression node)
        {
            //if(node.Kind == GotoExpressionKind.Return)
            //{
            //    return Expression.Return(generatorReturn,
            //        GeneratorStateBuilder.New(Visit(node.Value), 0));
            //}
            return base.VisitGoto(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == replaceArgs)
                return args;
            //if (node == replaceStackItem)
            //    return StackItem;
            if (node == replaceContext)
                return Context;
            //if (node == replaceScriptInfo)
            //    return ScriptInfo;
            foreach(var l in lifted)
            {
                if (l.original == node)
                    return Expression.Field( l.box, "Value");
            }
            return base.VisitParameter(node);
        }

        private (LabelTarget label,int id) GetNextYieldJumpTarget()
        {
            int id = jumps.Count + 1;
            var label = Expression.Label(typeof(void), "next" + id);
            var r = (label, id);
            jumps.Add(r);
            return r;
        }

        protected override Exp VisitYield(YYieldExpression node)
        {
            var arg = Visit(node.Argument);
            var (label, id) = GetNextYieldJumpTarget();
            return Expression.Block(
                Expression.Return(generatorReturn, 
                    GeneratorStateBuilder.New( arg, id)),
                Expression.Label(label),
                nextValue
                );

        }

        //protected override Exp VisitRelay(YRelayExpression relayExpression)
        //{
        //    return relayExpression;
        //}

        protected override Exp VisitLambda(LambdaExpression yLambdaExpression)
        {
            return yLambdaExpression;
        }

        ///// <summary>
        ///// Yield requires flattening as you cannot jump in and out in middle
        ///// of call
        ///// </summary>
        ///// <param name="node"></param>
        ///// <returns></returns>
        //protected override Expression VisitCall(YCallExpression node)
        //{
        //    var rewritten = MethodRewriter.Rewrite(node);
        //    if (rewritten == node)
        //        return base.VisitCall(node);
        //    return Visit(rewritten);
        //}

        protected override Exp VisitTryCatchFinally(TryExpression node)
        {
            if (!node.HasYield())
            {
                return  base.VisitTryCatchFinally(node);
            }

            var hasFinally = node.Finally != null;
            var @catch = node.Catch;
            var hasCatch = @catch != null;

            LabelTarget catchLabel = null;
            int catchId = 0;
            LabelTarget finallyLabel = null;
            int finallyId = 0;

            var tryList = new YBlockBuilder();
            if (hasCatch)
            {
                (catchLabel, catchId) = GetNextYieldJumpTarget();
            }
            if (hasFinally)
            {
                (finallyLabel, finallyId) = GetNextYieldJumpTarget();
            }


            var (endLabel, endId) = GetNextYieldJumpTarget();


            tryList.AddExpression(ClrGeneratorV2Builder.Push(pe, catchId, finallyId, endId));
            tryList.AddExpression(Visit(node.Try));
            tryList.AddExpression(Expression.Goto(hasFinally ? finallyLabel : endLabel));


            if (hasCatch) {
                tryList.AddExpression(Expression.Label(catchLabel));
                tryList.AddExpression(ClrGeneratorV2Builder.BeginCatch(pe));
                tryList.AddExpression(Expression.Assign(Visit(@catch.Parameter), exception));
                tryList.AddExpression(Visit(@catch.Body));
                tryList.AddExpression(YExpression.Empty);
                tryList.AddExpression(Expression.Goto(hasFinally ? finallyLabel : endLabel));
            }

            if (hasFinally)
            {
                tryList.AddExpression(Expression.Label(finallyLabel));
                tryList.AddExpression(ClrGeneratorV2Builder.BeginFinally(pe));
                tryList.AddExpression(Visit(node.Finally));
                tryList.AddExpression(ClrGeneratorV2Builder.Throw(pe, endId));
            }
            tryList.AddExpression(Expression.Label(endLabel));
            tryList.AddExpression(ClrGeneratorV2Builder.Pop(pe));
            var b = tryList.Build();
            return b;
        }

    }

    public class GeneratorStateBuilder
    {
        private static Type type = typeof(GeneratorState);

        private static ConstructorInfo _newFromValue =
            type.PublicConstructor(typeof(JSValue), typeof(int));

        public static Expression New(Expression value, int id)
        {
            return Expression.New(_newFromValue, value, Expression.Constant(id));
        }

        public static Expression New(int id)
        {
            return Expression.New(_newFromValue, JSUndefinedBuilder.Value, Expression.Constant(id));
        }

    }

    public class ClrGeneratorV2Builder
    {
        private static Type type = typeof(ClrGeneratorV2);

        private static MethodInfo _throw = type.PublicMethod(nameof(ClrGeneratorV2.Throw), typeof(int));
        private static MethodInfo _beginCatch = type.PublicMethod(nameof(ClrGeneratorV2.BeginCatch));
        private static MethodInfo _beginFinally = type.PublicMethod(nameof(ClrGeneratorV2.BeginFinally));

        private static MethodInfo _push = type.PublicMethod(
            nameof(ClrGeneratorV2.PushTry),
            typeof(int),
            typeof(int),
            typeof(int));

        private static MethodInfo _pop = type.PublicMethod(
            nameof(ClrGeneratorV2.Pop));


        private static MethodInfo _GetVariable
            = type.GetMethod("GetVariable");
        private static MethodInfo _InitVariables
            = type.GetMethod("InitVariables");


        public static Expression Push(Expression exp, int c, int f, int e)
        {
            return Expression.Call(exp, _push,
                Expression.Constant(c), 
                Expression.Constant(f),
                Expression.Constant(e));
        }

        internal static Expression GetVariable(ParameterExpression pe, int id, Type type)
        {
            return Expression.Call(pe, _GetVariable.MakeGenericMethod(type), Expression.Constant(id));
        }

        internal static Expression InitVariables(ParameterExpression pe, int count)
        {
            return Expression.Call(pe, _InitVariables, Expression.Constant(count));
        }

        internal static Expression Pop(ParameterExpression pe)
        {
            return Expression.Call(pe, _pop);
        }
        internal static Expression BeginCatch(ParameterExpression pe)
        {
            return Expression.Call(pe, _beginCatch);
        }
        internal static Expression BeginFinally(ParameterExpression pe)
        {
            return Expression.Call(pe, _beginFinally);
        }
        internal static Expression Throw(ParameterExpression pe, int id)
        {
            return Expression.Call(pe, _throw, Expression.Constant(id));
        }
    }
}
