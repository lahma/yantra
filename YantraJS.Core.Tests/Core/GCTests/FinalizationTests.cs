﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YantraJS.Core.Weak;
using YantraJS.Tests.Core;

namespace YantraJS.Core.Tests.Core.GCTests
{
    [TestClass]
    public class FinalizationTests : BaseTest
    {

        [TestMethod]
        public void FinalizeTest()
        {
            var success = false;


            var fr = new JSFinalizationRegistry(new JSFunction((in Arguments a) =>
            {
                success = true;
                return JSUndefined.Value;
            }));

            Register(fr);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();

            Assert.IsTrue(success);

        }

        private static void Register(JSFinalizationRegistry fr)
        {
            JSFinalizationRegistry.Register(new Arguments(fr, new JSObject(), new JSString("a")));
        }
    }
}
