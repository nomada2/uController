﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Web.Framework
{
    public partial class HttpHandler
    {
        private static readonly MethodInfo ExecuteAsyncMethodInfo = GetMethodInfo<Func<object, HttpContext, Task>>((result, httpContext) => ExecuteResultAsync(result, httpContext));
        private static readonly MethodInfo ChangeTypeMethodInfo = GetMethodInfo<Func<object, Type, object>>((value, type) => Convert.ChangeType(value, type));
        private static readonly MethodInfo JsonDeserializeMethodInfo = GetMethodInfo<Func<JsonTextReader, Type, object>>((jsonReader, type) => JsonDeserialize(jsonReader, type));
        private static readonly MethodInfo ActivatorMethodInfo = GetMethodInfo<Func<IServiceProvider, Type, object>>((sp, type) => CreateInstance(sp, type));
        private static readonly MethodInfo GetRequiredServiceMethodInfo = GetMethodInfo<Func<IServiceProvider, Type, object>>((sp, type) => sp.GetRequiredService(type));
        private static readonly MethodInfo ConvertToTaskMethodInfo = typeof(HttpHandler).GetMethod(nameof(ConvertTask), BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MemberInfo CompletedTaskMemberInfo = GetMemberInfo<Func<Task>>(() => Task.CompletedTask);

        private static ConcurrentDictionary<Type, Func<RequestDelegate, RequestDelegate>> _cache = new ConcurrentDictionary<Type, Func<RequestDelegate, RequestDelegate>>();

        public static Func<RequestDelegate, RequestDelegate> Build<THttpHandler>()
        {
            return Build(typeof(THttpHandler));
        }

        public static Func<RequestDelegate, RequestDelegate> Build(Type handlerType)
        {
            return _cache.GetOrAdd(handlerType, type => BuildWithoutCache(type));
        }

        private static Func<RequestDelegate, RequestDelegate> BuildWithoutCache(Type handlerType)
        {
            var methods = handlerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            var bindings = new List<Binding>();

            foreach (var method in methods)
            {
                var needForm = false;
                var attribute = method.GetCustomAttribute<HttpMethodAttribute>();
                var httpMethod = attribute?.Method ?? "";
                var template = attribute?.Template;
                var parameters = method.GetParameters();

                // Non void return type

                // Task Invoke(HttpContext context, RequestDelegate next)
                // {
                //     // The type is activated via DI if it has args
                //     return ExecuteResult(new THttpHandler(...).Method(..), httpContext);
                // }

                // void return type

                // Task Invoke(HttpContext context, RequestDelegate next)
                // {
                //     new THttpHandler(...).Method(..)
                //     return Task.CompletedTask;
                // }

                var httpContextArg = Expression.Parameter(typeof(HttpContext), "httpContext");
                var nextArg = Expression.Parameter(typeof(RequestDelegate), "next");
                var requestServicesExpr = Expression.Property(httpContextArg, nameof(HttpContext.RequestServices));

                // Fast path: We can skip the activator if there's only a default ctor with 0 args
                var ctors = handlerType.GetConstructors();

                Expression httpHandlerExpression = null;
                if (ctors.Length == 1 && ctors[0].GetParameters().Length == 0)
                {
                    httpHandlerExpression = Expression.New(ctors[0]);
                }
                else
                {
                    // (THttpHandler)ActivatorUtilities.CreateInstance(
                    //            context.RequestServices, 
                    //            typeof(THttpHandler));
                    httpHandlerExpression = Expression.Convert(
                                                Expression.Call(ActivatorMethodInfo,
                                                                requestServicesExpr,
                                                                Expression.Constant(handlerType)),
                                                handlerType);
                }

                var args = new List<Expression>();

                var httpRequestExpr = Expression.Property(httpContextArg, nameof(HttpContext.Request));

                foreach (var p in parameters)
                {
                    var fromQuery = p.GetCustomAttribute<FromQueryAttribute>();
                    var fromHeader = p.GetCustomAttribute<FromHeaderAttribute>();
                    var fromForm = p.GetCustomAttribute<FromFormAttribute>();
                    var fromBody = p.GetCustomAttribute<FromBodyAttribute>();
                    var fromRoute = p.GetCustomAttribute<FromRouteAttribute>();
                    var fromCookie = p.GetCustomAttribute<FromCookieAttribute>();
                    var fromService = p.GetCustomAttribute<FromServicesAttribute>();

                    Expression paramterExpression = Expression.Default(p.ParameterType);

                    if (fromQuery != null)
                    {
                        var queryProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Query));
                        paramterExpression = BindArgument(queryProperty, p, fromQuery.Name);
                    }
                    else if (fromHeader != null)
                    {
                        var headersProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Headers));
                        paramterExpression = BindArgument(headersProperty, p, fromHeader.Name);
                    }
                    else if (fromRoute != null)
                    {
                        var featuresProperty = Expression.Property(httpContextArg, nameof(HttpContext.Features));
                        var routeFeatureVar = Expression.Convert(Expression.MakeIndex(featuresProperty, featuresProperty.Type.GetProperty("Item"), new[] { Expression.Constant(typeof(IRoutingFeature)) }), typeof(IRoutingFeature));
                        var routeDataVar = Expression.Property(routeFeatureVar, nameof(IRoutingFeature.RouteData));
                        var routeValuesVar = Expression.Property(routeDataVar, nameof(RouteData.Values));
                        paramterExpression = BindArgument(routeValuesVar, p, fromRoute.Name);
                    }
                    else if (fromCookie != null)
                    {
                        var cookiesProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Cookies));
                        paramterExpression = BindArgument(cookiesProperty, p, fromCookie.Name);
                    }
                    else if (fromService != null)
                    {
                        paramterExpression = Expression.Convert(
                             Expression.Call(GetRequiredServiceMethodInfo,
                                             requestServicesExpr,
                                             Expression.Constant(p.ParameterType)),
                             p.ParameterType);
                    }
                    else if (fromForm != null)
                    {
                        needForm = true;

                        var formProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Form));
                        paramterExpression = BindArgument(formProperty, p, fromForm.Name);
                    }
                    else if (fromBody != null)
                    {
                        var bodyProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Body));
                        paramterExpression = BindBody(bodyProperty, p);
                    }
                    else
                    {
                        if (p.ParameterType == typeof(IFormCollection))
                        {
                            paramterExpression = Expression.Property(httpRequestExpr, nameof(HttpRequest.Form));
                        }
                        else if (p.ParameterType == typeof(HttpContext))
                        {
                            paramterExpression = httpContextArg;
                        }
                        else if (p.ParameterType == typeof(RequestDelegate))
                        {
                            paramterExpression = nextArg;
                        }
                        else if (p.ParameterType == typeof(IHeaderDictionary))
                        {
                            paramterExpression = Expression.Property(httpRequestExpr, nameof(HttpRequest.Headers));
                        }
                    }

                    args.Add(paramterExpression);
                }

                Expression body = null;

                if (method.ReturnType == typeof(void))
                {
                    var bodyExpressions = new List<Expression>
                    {
                        Expression.Call(httpHandlerExpression, method, args),
                        Expression.Property(null, (PropertyInfo)CompletedTaskMemberInfo)
                    };

                    body = Expression.Block(bodyExpressions);
                }
                else
                {
                    var methodCall = Expression.Call(httpHandlerExpression, method, args);

                    // Coerce Task<T> to Task<object>
                    if (method.ReturnType.IsGenericType &&
                        method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var typeArg = method.ReturnType.GetGenericArguments()[0];

                        // Convert<T>(handler.Method(..))
                        methodCall = Expression.Call(
                                           ConvertToTaskMethodInfo.MakeGenericMethod(typeArg),
                                           methodCall);
                    }

                    body = Expression.Call(ExecuteAsyncMethodInfo, methodCall, httpContextArg);
                }

                var lambda = Expression.Lambda<Func<HttpContext, RequestDelegate, Task>>(body, httpContextArg, nextArg);

                bindings.Add(new Binding
                {
                    Invoke = lambda.Compile(),
                    NeedForm = needForm,
                    HttpMethod = httpMethod,
                    Template = template
                });
            }

            return next =>
            {
                return async context =>
                {
                    var binding = Match(context, bindings);

                    if (binding != null)
                    {
                        // Generating async code would just be insane so if the method needs the form populate it here
                        // so the within the method it's cached
                        if (binding.NeedForm)
                        {
                            await context.Request.ReadFormAsync();
                        }

                        await binding.Invoke(context, next);
                        return;
                    }

                    await next(context);
                };
            };
        }

        private static Binding Match(HttpContext context, List<Binding> bindings)
        {
            Binding binding = null;
            var currentMaxScore = 0;

            foreach (var b in bindings)
            {
                int score = 0;
                if (string.Equals(context.Request.Method, b.HttpMethod, StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }

                if (b.Template != null && context.Request.Path.StartsWithSegments(b.Template, StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }

                if (score > currentMaxScore || binding == null)
                {
                    currentMaxScore = score;
                    binding = b;
                }
            }

            return binding;
        }

        private static Expression BindBody(Expression httpBody, ParameterInfo p)
        {
            // Hard coded to JSON (and JSON.NET at that!)
            // Also this is synchronous, good luck generating async anything
            // new JsonSerializer().Deserialize(
            //     new JsonTextReader(
            //         new HttpRequestStreamReader(
            //            context.Request.Body, Encoding.UTF8)), p.ParameterType);
            //
            var streamReaderCtor = typeof(HttpRequestStreamReader).GetConstructor(new[] { typeof(Stream), typeof(Encoding) });
            var streamReader = Expression.New(streamReaderCtor, httpBody, Expression.Constant(Encoding.UTF8));

            var textReaderCtor = typeof(JsonTextReader).GetConstructor(new[] { typeof(TextReader) });
            var textReader = Expression.New(textReaderCtor, streamReader);

            Expression expr = Expression.Call(JsonDeserializeMethodInfo, textReader, Expression.Constant(p.ParameterType));
            expr = Expression.Convert(expr, p.ParameterType);

            return expr;
        }

        private static Expression BindArgument(MemberExpression property, ParameterInfo parameter, string name)
        {
            string key = name ?? parameter.Name;
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var valueArg = Expression.Convert(Expression.MakeIndex(property, property.Type.GetProperty("Item"), new[] { Expression.Constant(key) }), typeof(string));

            var parseMethod = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                  .FirstOrDefault(m => m.Name == "Parse" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));

            Expression expr = null;
            if (parseMethod != null)
            {
                expr = Expression.Call(parseMethod, valueArg);
            }
            else
            {
                // Convert.ChangeType()
                expr = Expression.Call(ChangeTypeMethodInfo, valueArg, Expression.Constant(parameter.ParameterType));
            }

            if (expr.Type != parameter.ParameterType)
            {
                expr = Expression.Convert(expr, parameter.ParameterType);
            }

            // property[key] == null ? default : (ParameterType){Type}.Parse(property[key]);
            expr = Expression.Condition(
                Expression.Equal(valueArg, Expression.Constant(null)),
                Expression.Default(parameter.ParameterType),
                expr);

            return expr;
        }

        private static MethodInfo GetMethodInfo<T>(Expression<T> expr)
        {
            var mc = (MethodCallExpression)expr.Body;
            return mc.Method;
        }

        private static MemberInfo GetMemberInfo<T>(Expression<T> expr)
        {
            var mc = (MemberExpression)expr.Body;
            return mc.Member;
        }

        private static object CreateInstance(IServiceProvider sp, Type type)
        {
            return ActivatorUtilities.CreateInstance(sp, type);
        }

        private static object JsonDeserialize(JsonTextReader jsonReader, Type type)
        {
            return new JsonSerializer().Deserialize(jsonReader, type);
        }

        private static async Task<object> ConvertTask<T>(Task<T> task)
        {
            return await task;
        }

        private static async Task ExecuteResultAsync(object result, HttpContext httpContext)
        {
            switch (result)
            {
                case Task<object> task:
                    {
                        var val = await task;
                        // We normalize to Task<object> then we execute the actual result
                        await ExecuteResultAsync(val, httpContext);
                    }
                    break;
                case Task task:
                    await task;
                    break;
                case Result val:
                    {
                        await val.ExecuteAsync(httpContext);
                    }
                    break;
                case RequestDelegate val:
                    await val(httpContext);
                    break;
                default:
                    {
                        var val = new JsonResult(result);
                        await val.ExecuteAsync(httpContext);
                    }
                    break;
            }
        }

        private class Binding
        {
            public Func<HttpContext, RequestDelegate, Task> Invoke { get; set; }

            public string Template { get; set; }

            public string HttpMethod { get; set; }

            public bool NeedForm { get; set; }
        }
    }
}

