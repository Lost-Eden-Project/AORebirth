"""Focused offline review/merge checks; no installed GameData, engines or captures."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

SOURCE=Path(__file__).resolve().parents[1]/'MissionChestRKCatalog'/'prepare_approved_catalog.py'
SPEC=importlib.util.spec_from_file_location('prepare_approved_catalog',SOURCE)
CATALOG=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CATALOG)


class ReviewedCatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.root=Path(self.temp.name)
        self.inputs=self.root/'cache'; self.inputs.mkdir()
        self.data=self.root/'data'; self.data.mkdir()
        self.output=self.root/'output'
        (self.data/'items.dat').write_bytes(b'offline-fixture')
        self.write(self.data/'ItemTemplates.json',{'F':{'Templates':[1,2]},'G':{'Templates':[3,4]}})
        self.items=[{'Id':id,'Name':'fixture','Quality':ql,'Flags':0,'ReadComplete':True,
            'BytesLeft':0,'NanoTemplate':False,'NamedStats':{'Can':1},'Criteria':[]}
            for id,ql in [(1,1),(2,10),(3,20),(4,30)]]
        self.write(self.inputs/'metadata-enums.json',{'ItemFlags':{'Special':1,'Disabled':2,
            'Stationary':4,'NoDrop':8,'Unique':16},'CanFlags':{'Carry':1}})
        for name in ['MissionChestSeasonalExclusions.json','MissionChestSpecialExclusions.json']:
            self.write(self.inputs/name,{'Version':1,'Entries':[]})
        self.review={'RuleId':'captured-pair','TemplateIds':[1,2],'FamilyHash':'F','Category':'Supplies',
            'RkEvidence':['capture#world/owner/slot'],'OrdinaryEvidence':['authored ordinary review']}
        self.reviews=self.root/'private-reviews.json'
        self.refresh()

    def tearDown(self):
        self.temp.cleanup()

    def write(self,path,value):
        path.write_text(json.dumps(value),encoding='utf-8')

    def refresh(self,rows=None):
        source=self.inputs/'item-metadata.jsonl'
        source.write_text(''.join(json.dumps(item)+'\n' for item in self.items),encoding='utf-8')
        self.write(self.inputs/'item-metadata-source.json',{'OutputSha256':CATALOG.sha(source),
            'SourceSha256':CATALOG.sha(self.data/'items.dat'),'RecordCount':len(self.items)})
        self.write(self.reviews,{'Version':1,'ItemsSha256':CATALOG.sha(self.data/'items.dat'),
            'FamiliesSha256':CATALOG.sha(self.data/'ItemTemplates.json'),'Reviews':rows or [self.review]})

    def prepare(self,**kwargs):
        with contextlib.redirect_stdout(io.StringIO()):
            CATALOG.prepare(self.inputs,self.data,self.output,self.reviews,**kwargs)
        return CATALOG.read(self.output/'MissionChestRKAllowlist.json')

    def exception(self,**kwargs):
        self.review['MetadataExceptions']={'Reason':'exact captured ordinary item false positive',
            'Evidence':['capture#exact-owner/inventory/slot'],**kwargs}

    def prior(self):
        policy=self.root/'prior-policy.json'
        entries=[{'LowId':1,'HighId':2,'MinQuality':low,'MaxQuality':high,
            'Classification':'OrdinaryRubiKa','Evidence':['retained proof'],'Description':'retained',
            'Extra':{'retain':'exact value'}} for low,high in [(1,5),(8,10)]]
        self.write(policy,{'Version':1,'Entries':entries})
        results=self.root/'prior-results.json'
        definitions=[{'Id':id,'Name':'retained name','StoredQuality':ql,'Category':'Supplies',
            'RuleId':'retained-rule','FamilyHash':'F'} for id,ql in [(1,1),(2,10)]]
        self.write(results,{'Summary':{'AllowlistSha256':CATALOG.sha(policy),
            'ItemsSha256':CATALOG.sha(self.data/'items.dat'),
            'FamiliesSha256':CATALOG.sha(self.data/'ItemTemplates.json')},
            'ApprovedDefinitions':definitions,'Rejected':[]})
        return policy,results,entries,definitions

    def test_explicit_private_reviews_reuse_cache_and_default_path_still_works(self):
        result=self.prepare()
        self.assertEqual((result['Entries'][0]['MinQuality'],result['Entries'][0]['MaxQuality']),(1,10))
        (self.inputs/'ordinary-family-reviews.json').write_bytes(self.reviews.read_bytes())
        with contextlib.redirect_stdout(io.StringIO()):
            CATALOG.prepare(self.inputs,self.data,self.root/'default-output')
        self.assertEqual(result,CATALOG.read(self.root/'default-output/MissionChestRKAllowlist.json'))

    def test_additive_merge_preserves_rows_and_categories_and_fills_only_gap(self):
        policy,results,entries,definitions=self.prior()
        new={**self.review,'RuleId':'new-pair','TemplateIds':[3,4],'FamilyHash':'G'}
        self.refresh([self.review,new])
        before=(policy.read_bytes(),results.read_bytes())
        result=self.prepare(existing_policy=policy,existing_review_results=results)
        self.assertEqual(result['Entries'][:2],entries)
        self.assertEqual([(e['LowId'],e['MinQuality'],e['MaxQuality']) for e in result['Entries'][2:]],[(1,6,7),(3,20,30)])
        reviewed=CATALOG.read(self.output/'ordinary-rk-review-results.json')
        self.assertEqual(reviewed['ApprovedDefinitions'][:2],definitions)
        manifest=CATALOG.read(self.output/'ordinary-rk-merge-manifest.json')
        self.assertEqual((manifest['RetainedExistingBands'],manifest['AddedBands'],manifest['AddedDefinitions']),(2,2,2))
        self.assertEqual(before,(policy.read_bytes(),results.read_bytes()))

    def test_additive_category_conflict_and_unbound_prior_results_fail(self):
        policy,results,_,_=self.prior()
        self.review['Category']='Weapons'; self.refresh()
        with self.assertRaisesRegex(ValueError,'category'):
            self.prepare(existing_policy=policy,existing_review_results=results)
        self.review['Category']='Supplies'; self.refresh()
        prior=CATALOG.read(results); prior['Summary']['AllowlistSha256']='changed'; self.write(results,prior)
        with self.assertRaisesRegex(ValueError,'exact sources'):
            self.prepare(existing_policy=policy,existing_review_results=results)

    def test_default_flags_rejected_and_only_authored_special_nodrop_can_pass(self):
        for name,flag in [('Special',1),('NoDrop',8)]:
            self.items[0]['Flags']=flag; self.review.pop('MetadataExceptions',None); self.refresh()
            self.assertEqual(self.prepare()['Entries'],[])
            self.exception(AllowedFlags=[name]); self.refresh()
            self.assertEqual(len(self.prepare()['Entries']),1)
        self.items[0]['Flags']=1|16; self.exception(AllowedFlags=['Special']); self.refresh()
        self.assertEqual(self.prepare()['Entries'],[])

    def test_no_exception_for_unique_disabled_stationary_or_missing_evidence(self):
        for name in ['Unique','Disabled','Stationary']:
            self.exception(AllowedFlags=[name]); self.refresh()
            with self.assertRaises(ValueError): self.prepare()
        self.items[0]['Flags']=1; self.exception(AllowedFlags=['Special']); self.review['MetadataExceptions']['Evidence']=[]; self.refresh()
        with self.assertRaises(ValueError): self.prepare()

    def target_criteria(self,selector='OnTarget'):
        return [{'Source':'Action:ToUse','Index':0,'StatName':'Flags','OperatorName':selector},
            {'Source':'Action:ToUse','Index':1,'StatName':'Flags','OperatorName':'And'},
            {'Source':'Action:ToUse','Index':2,'StatName':'NPCFamily','OperatorName':'EqualTo','Target':19}]

    def test_exact_target_npcfamily_exception_uses_selector_not_raw_target(self):
        self.items[0]['Criteria']=self.target_criteria(); self.refresh()
        self.assertEqual(self.prepare()['Entries'],[])
        self.exception(TargetCriteria=[{'StatName':'NPCFamily','TermIndex':2,'Target':'OnTarget'}]); self.refresh()
        self.assertEqual(len(self.prepare()['Entries']),1)
        self.items[0]['Criteria']=self.target_criteria('OnUser'); self.refresh()
        with self.assertRaisesRegex(ValueError,'OnTarget'): self.prepare()

    def test_target_subject_resets_for_next_ordered_group_and_ambiguous_index_fails(self):
        self.exception(TargetCriteria=[{'StatName':'NPCFamily','TermIndex':2,'Target':'OnTarget'}])
        self.items[0]['Criteria']=self.target_criteria()+[
            {'Source':'Event:OnUse/1','Index':0,'StatName':'Flags','OperatorName':'And'},
            {'Source':'Event:OnUse/1','Index':2,'StatName':'NPCFamily','OperatorName':'EqualTo'}]
        self.refresh()
        with self.assertRaisesRegex(ValueError,'ambiguous'): self.prepare()

    def test_target_exception_does_not_waive_direct_or_other_restricted_stats(self):
        self.items[0]['Criteria']=self.target_criteria()
        self.exception(TargetCriteria=[{'StatName':'NPCFamily','TermIndex':2,'Target':'OnTarget'}])
        for stat in ['NPCFamily','Expansion','QuestStat','GmLevel']:
            self.items[0]['NamedStats']={'Can':1,stat:1}; self.refresh()
            self.assertEqual(self.prepare()['Entries'],[])

    def test_exclusion_catalog_cannot_be_waived(self):
        self.items[0]['Flags']=1; self.exception(AllowedFlags=['Special']); self.refresh()
        self.write(self.inputs/'MissionChestSpecialExclusions.json',{'Version':1,'Entries':[{'TemplateIds':[1]}]})
        self.assertEqual(self.prepare()['Entries'],[])


if __name__=='__main__':
    unittest.main()
