// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace BuildXL.Utilities
{
    /// <summary>
    /// Used to specify out paramater to async method.
    /// </summary>
    public sealed class AsyncOut<T>
    {
        /// <summary>
        /// The value
        /// </summary>
        public T Value;

        /// <nodoc />
        public static implicit operator T(AsyncOut<T> value)
        {
            return value.Value;
        }

        /// <nodoc />
        public AsyncOut()
        {
        }

        /// <summary>
        /// Convenience constructor to all using with target typed new.
        /// </summary>
        public AsyncOut(out AsyncOut<T> @this)
        {
            @this = this;
        }

        /// <summary>
        /// Sets the value
        /// </summary>
        public void Set(T value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// Helper methods for <see cref="AsyncOut{T}"/>
    /// </summary>
    public static class AsyncOut
    {
        /// <summary>
        /// Allows inline declaration of <see cref="AsyncOut{T}"/> patterns like the
        /// (out T parameter) pattern. Usage: await ExecuteAsync(out AsyncOut.Var&lt;T&gt;(out var outParam));
        /// </summary>
        public static AsyncOut<T> Var<T>(out AsyncOut<T> value)
        {
            value = new AsyncOut<T>();
            return value;
        }
    }
}
