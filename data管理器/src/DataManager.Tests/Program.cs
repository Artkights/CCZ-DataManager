namespace DataManager.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        DataManager.App.ApplicationBootstrap.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            if (args.FirstOrDefault() == "--core-algorithm-performance")
            {
                CoreAlgorithmPerformanceTests.Run(Path.GetFullPath(args[1]), args.Length > 2 ? args[2] : "after");
                return 0;
            }
            if (args.FirstOrDefault() == "--workbook-performance")
            {
                WorkbookPerformanceTests.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.FirstOrDefault() == "--performance-self-audit")
            {
                PerformanceSelfAuditTests.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.FirstOrDefault() == "--workbook-native")
            {
                WorkbookNativeTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                return 0;
            }
            if (args.FirstOrDefault() == "--scenario-effects")
            {
                ScenarioEffectAssignmentTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                Console.WriteLine("SCENARIO_EFFECT_ASSIGNMENT_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--page-workbook")
            {
                PageWorkbookTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                WorkbookNativeTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                return 0;
            }
            if (args.FirstOrDefault() == "--save-history-21")
            {
                SaveHistory21RegressionTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                Console.WriteLine("SAVE_HISTORY_21_REGRESSION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-font-scale")
            {
                UiFontScaleTests.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-full-page-layout")
            {
                UiFullPageLayoutTests.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-person-layout")
            {
                UiOptimizationReportTests.Run(args[1], args[2], personOnly: true);
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-optimization-report")
            {
                UiOptimizationReportTests.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-cleanup-navigation")
            {
                UiCleanupNavigationTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                return 0;
            }
            if (args.FirstOrDefault() == "--equipment-types")
            {
                EquipmentTypeCatalogTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                Console.WriteLine("EQUIPMENT_TYPE_CATALOG_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--item-semantic-editing")
            {
                ItemSemanticEditingTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory());
                return 0;
            }
            if (args.FirstOrDefault() == "--range-preview-compatibility")
            {
                RangePreviewCompatibilityTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory());
                return 0;
            }
            if (args.FirstOrDefault() == "--resource-previews")
            {
                ResourcePreviewTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) :
                    Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                Console.WriteLine("RESOURCE_PREVIEW_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--animation-representative-frames")
            {
                AnimationRepresentativeFrameTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory());
                return 0;
            }
            if (args.FirstOrDefault() == "--page-loading")
            {
                PageLoadingTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版"));
                return 0;
            }
            var basePath = Environment.GetEnvironmentVariable("DATAMANAGER_TEST_BASE")
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "基底", "加强版6.5未加密版"));
            if (args.Length > 1 && args[0] is "--ui-integration" or "--ui-regression" or "--core-regression" or "--migration-regression" or "--writeback-hardening")
                basePath = Path.GetFullPath(args[1]);
            if (args.FirstOrDefault() == "--extend-core")
            {
                ProjectRootLocatorTests.Run();
                ExtendDataTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("EXTEND_DATA_CORE_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--extend-compatibility")
            {
                ExtendDataCompatibilityTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("EXTEND_DATA_COMPATIBILITY_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--extend-ui")
            {
                ExtendDataUiTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath, args.Length > 2 ? Path.GetFullPath(args[2]) : null);
                Console.WriteLine("EXTEND_DATA_UI_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--extend-real-matrix")
            {
                var repository = args.Length > 1 ? Path.GetFullPath(args[1]) : TestSupport.RepositoryRootFromBase(basePath);
                var output = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(repository, "data管理器", "验证", "拓展数据完善-20261019");
                ExtendDataRealBaseMatrixTests.Run(repository, output);
                return 0;
            }
            if (args.FirstOrDefault() == "--writeback-hardening")
            {
                TextConstraintTests.Run();
                ByteTextInputTests.Run();
                WritebackHardeningTests.Run(basePath);
                DraftOperationAtomicityTests.Run(basePath);
                TextPipelineWritebackTests.Run(basePath);
                UiWritebackHardeningTests.Run(basePath);
                Console.WriteLine("WRITEBACK_HARDENING_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--field-writeback-matrix")
            {
                FieldWritebackMatrixTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("FIELD_WRITEBACK_MATRIX_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--draft-operation-atomicity")
            {
                DraftOperationAtomicityTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                return 0;
            }
            if (args.FirstOrDefault() == "--text-pipeline-writeback")
            {
                TextPipelineWritebackTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-resource-validation")
            {
                MigrationResourceValidationTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                MigrationVerificationTransactionTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("MIGRATION_RESOURCE_VALIDATION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-writeback-hardening")
            {
                ByteTextInputTests.Run();
                UiWritebackHardeningTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                return 0;
            }
            if (args.FirstOrDefault() == "--resource-infrastructure")
            {
                ResourceInfrastructureTests.Run();
                Console.WriteLine("RESOURCE_INFRASTRUCTURE_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-presentation")
            {
                UiPresentationTests.Run(basePath);
                Console.WriteLine("UI_PRESENTATION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--atlas-dialog")
            {
                AtlasImportDialogTests.Run();
                Console.WriteLine("ATLAS_IMPORT_DIALOG_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--animation-preview-ui")
            {
                AnimationPreviewUiTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("ANIMATION_PREVIEW_UI_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-ui")
            {
                MigrationUiTests.Run();
                Console.WriteLine("MIGRATION_UI_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--job-attributes-ui" && args.Length > 1)
            {
                WinFormsSmokeTests.RunJobAttributesIntegration(Path.GetFullPath(args[1]));
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-performance")
            {
                MigrationFieldIsolationPerformanceTests.Run();
                Console.WriteLine("MIGRATION_PERFORMANCE_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-bulk" && args.Length > 1)
            {
                SaveBulkPerformanceTests.Run(args[1]);
                Console.WriteLine("MIGRATION_BULK_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-legacy" && args.Length > 1)
            {
                LegacyDynamicLayoutTests.Run(args[1]);
                Console.WriteLine("LEGACY_DYNAMIC_LAYOUT_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-readonly" && args.Length > 1)
            {
                ReadonlyResourceRollbackTests.Run(args[1]);
                Console.WriteLine("READONLY_RESOURCE_ROLLBACK_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-resources")
            {
                MigrationResourceTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("MIGRATION_RESOURCE_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-icons" && args.Length > 2)
            {
                ImageMigrationConversionTests.Run(args[1], args[2]);
                Console.WriteLine("MIGRATION_IMAGE_CONVERSION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-real" && args.Length >= 4)
            {
                RealProjectMigrationTests.Run(args[1], args[2], args[3], args.Length > 4 ? args[4] : "apply");
                return 0;
            }
            if (args.FirstOrDefault() == "--resource-bindings")
            {
                ResourceBindingMappingTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("RESOURCE_BINDING_MAPPING_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--unified-history")
            {
                UnifiedHistoryTests.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : basePath);
                Console.WriteLine("UNIFIED_HISTORY_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() is "--ui-integration" or "--ui-regression")
            {
                WinFormsSmokeTests.RunProjectIntegration(basePath);
                if (args[0] == "--ui-regression") ResourcePreviewTests.Run(basePath);
                Console.WriteLine("UI_INTEGRATION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--migration-regression")
            {
                MigrationFieldIsolationPerformanceTests.Run();
                ReadonlyResourceRollbackTests.Run(basePath);
                AdvancedFeatureTests.Run(basePath);
                Console.WriteLine("MIGRATION_REGRESSION_TESTS_OK");
                return 0;
            }
            if (args.FirstOrDefault() == "--core-regression")
            {
                FixtureSnapshotTests.Run(basePath);
                ProfileTests.Run();
                RecordCodecTests.Run();
                PersonQuoteTests.Run(basePath);
                AssignmentEvidenceTests.Run(basePath);
                DomainCodecTests.Run(basePath);
                BusinessTableWriteTests.Run(basePath);
                AccessoryGroupCsvTests.Run(basePath);
                ScannerTests.Run(basePath);
                WorkspaceTests.Run(basePath);
                CsvTests.Run(basePath);
                HistoryTests.Run(basePath);
                UnifiedHistoryTests.Run(basePath);
                ScanMatrixTests.Run(basePath);
                ProfileWriteTests.Run(basePath);
                Console.WriteLine("CORE_REGRESSION_TESTS_OK");
                return 0;
            }
            ItemTypeTests.Run();
            if (args.Length > 0 && args[0] == "--item-types")
            {
                if (args.Length > 1) ItemTypeTests.RunProject(Path.GetFullPath(args[1]));
                Console.WriteLine("ITEM_TYPE_TESTS_OK");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--resource-controls")
            {
                IntegratedResourcePanelTests.Run(Path.GetFullPath(args[1]));
                Console.WriteLine("RESOURCE_CONTROL_TESTS_OK");
                return 0;
            }
            ProfileTests.Run();
            RecordCodecTests.Run();
            ResourceInfrastructureTests.Run();
            ResourceBindingMappingTests.Run(basePath);
            var snapshotPath = args.Length == 2 && args[0] == "--ui-snapshot" ? Path.GetFullPath(args[1]) : null;
            WinFormsSmokeTests.Run(snapshotPath);
            if (Directory.Exists(basePath))
            {
                FixtureSnapshotTests.Run(basePath);
                ItemTypeTests.RunProject(basePath);
                IntegratedResourcePanelTests.Run(basePath);
                PersonQuoteTests.Run(basePath);
                AssignmentEvidenceTests.Run(basePath);
                WinFormsSmokeTests.RunProjectIntegration(basePath);
                ResourcePreviewTests.Run(basePath);
                DomainCodecTests.Run(basePath);
                BusinessTableWriteTests.Run(basePath);
                AccessoryGroupCsvTests.Run(basePath);
                ScannerTests.Run(basePath);
                WorkspaceTests.Run(basePath);
                CsvTests.Run(basePath);
                HistoryTests.Run(basePath);
                UnifiedHistoryTests.Run(basePath);
                ScanMatrixTests.Run(basePath);
                ProfileWriteTests.Run(basePath);
                AdvancedFeatureTests.Run(basePath);
            }
            Console.WriteLine("DATA_MANAGER_CORE_TESTS_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
