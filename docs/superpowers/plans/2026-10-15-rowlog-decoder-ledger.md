# Journal des décisions : décodeur RowLog Contents

Décisions prises seul pendant l'exécution autonome demandée le 15 octobre 2026, avec la raison et ce qu'elles coûtent si elles sont fausses. Ce journal sert à auditer le travail sans relire les transcripts.

## Avant le code

1. « external harm review » est lu comme la relecture externe du code (skill `external-code-review`) : aucun autre skill ne porte ce nom. Si c'était autre chose, la dernière étape est à refaire.
2. Trois panels de cinq relecteurs sur la spec (v1, v2, v3), puis une v4 corrigée sans quatrième panel : le panel du plan relit la spec comme référence et sert de quatrième passage. Coût si faux : un défaut de la v4 découvert pendant l'implémentation. Chaque panel a surtout trouvé des défauts dans les correctifs du précédent, ce qui justifiait les deux premières reprises.
3. L'ancre « ligne actuelle » (approche 3) est gardée, comme Rudi l'a validé, mais restreinte aux tables clustered sans ADR : en heap la recherche par `%%physloc%%` balaie la table, et sous ADR `DBCC PAGE` montre des octets annulés. Coût : pas de reconstruction arrière depuis la ligne actuelle dans ces deux cas.
4. In-Memory OLTP hors périmètre, mesuré à la demande de Rudi : `LOP_HK` opaque dans `fn_dblog`, lignes lisibles par `sys.fn_dblog_xtp` dans un format propre. Candidat pour une spec C.
5. Le plan n'a pas été « généré puis rejoué » à partir d'une implémentation jetable : il donne le code complet des composants purs, et les deux dispositions non documentées sont confiées à une tâche de recherche (tâche 4) avant le code qui en dépend. Le panel du plan compile et exécute ce code. Coût : des défauts de code du plan trouvés par les implémenteurs plutôt qu'avant.
6. Panel de cinq sur le plan (tag `78953a57d6ec`) : toutes les trouvailles retenues, sauf celles qui demandaient d'écrire le code de la tâche 4 avant la recherche. Le code des tâches 2, 5 et 6 corrigé a été recompilé et exécuté hors arbre (41 tests). Coût si faux : un défaut du plan trouvé par un implémenteur, ce que les briefs l'autorisent à signaler.
7. Spec v5 : un retour arrière partiel ADR (`LOP_CTR_NEST_ABORT` sans enregistrement de compensation) empoisonne toute la chaîne de la ligne, puis la règle a été restreinte aux seuls retours ADR, les compensations classiques arrêtant la chaîne à leur enregistrement. Coût si faux : des reconstructions refusées à tort, jamais une valeur fausse affichée.
8. Ancre 3 réécrite dans l'ordre de la spec §7.2 : la ligne est tenue (`HOLDLOCK`) avant la lecture du suffixe du journal, et le suffixe est ajouté au cache dans un rappel pendant que la ligne est tenue. Coût : une transaction SERIALIZABLE de quelques centaines de millisecondes sur la base analysée, au clic explicite seulement.
